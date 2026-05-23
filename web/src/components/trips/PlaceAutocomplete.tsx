"use client";

import { useEffect, useRef } from "react";
import { APIProvider, useMapsLibrary } from "@vis.gl/react-google-maps";
import { Input } from "@/components/ui/input";
import type { LatLng } from "@/lib/api/schema";

export interface SelectedPlace {
  address: string;
  location: LatLng | null;
}

export interface PlaceAutocompleteProps {
  id?: string;
  value: string;
  placeholder?: string;
  /** Fires whenever the controlled value changes (on selection only, since the
   *  new web component manages its own input). */
  onChange: (next: string) => void;
  /** Fires when the user picks a suggestion from the Google dropdown. */
  onPlace: (place: SelectedPlace) => void;
  /** Country code(s) to bias suggestions. Defaults to "au" (Australia). */
  country?: string | string[];
}

/**
 * Google Maps-style address input. Uses Google's new `PlaceAutocompleteElement`
 * web component (required for Cloud accounts created after March 2025). The
 * element renders its own input + suggestions dropdown — we can't bind to a
 * shadcn `<Input>` like the legacy `Autocomplete` widget allowed — but the
 * dropdown UX is identical to maps.google.com's search.
 *
 * Note: because the element manages its own value, the `value` prop is used
 * only for the no-key fallback render. When the Google SDK is loaded, the
 * element is the source of truth for the input text, and parent form state is
 * updated via `onChange` + `onPlace` on selection.
 */
export function PlaceAutocomplete(props: PlaceAutocompleteProps): React.JSX.Element {
  const apiKey = process.env.NEXT_PUBLIC_GOOGLE_MAPS_KEY;
  if (!apiKey) {
    return (
      <Input
        id={props.id}
        value={props.value}
        placeholder={props.placeholder}
        onChange={(e) => props.onChange(e.target.value)}
      />
    );
  }
  return (
    <APIProvider apiKey={apiKey} libraries={["places"]}>
      <PlaceAutocompleteInner {...props} />
    </APIProvider>
  );
}

interface PlacePredictionLike {
  toPlace: () => google.maps.places.Place;
}

function PlaceAutocompleteInner({
  id,
  placeholder,
  onChange,
  onPlace,
  country = "au",
}: PlaceAutocompleteProps): React.JSX.Element {
  const placesLibrary = useMapsLibrary("places");
  const containerRef = useRef<HTMLDivElement | null>(null);

  // Stable refs to the latest callbacks so the event listener — registered
  // once when the library loads — always sees fresh closures. Updated via an
  // effect so we don't mutate refs during render.
  const onChangeRef = useRef(onChange);
  const onPlaceRef = useRef(onPlace);
  useEffect(() => {
    onChangeRef.current = onChange;
    onPlaceRef.current = onPlace;
  });

  useEffect(() => {
    if (!placesLibrary || !containerRef.current) return;

    const regionCodes = Array.isArray(country) ? country : [country];
    // The constructor signature isn't fully typed in @types/google.maps yet
    // (the API is recent — March 2025); use a small assertion at the boundary.
    const Ctor = (placesLibrary as unknown as {
      PlaceAutocompleteElement: new (opts: {
        includedRegionCodes?: string[];
      }) => HTMLElement & {
        addEventListener(
          type: "gmp-select",
          listener: (ev: Event & { placePrediction: PlacePredictionLike }) => void,
        ): void;
        removeEventListener(type: "gmp-select", listener: EventListener): void;
      };
    }).PlaceAutocompleteElement;

    const element = new Ctor({ includedRegionCodes: regionCodes });
    if (id) element.setAttribute("id", id);
    if (placeholder) element.setAttribute("placeholder", placeholder);
    element.classList.add("place-autocomplete-element");

    async function handleSelect(
      ev: Event & { placePrediction: PlacePredictionLike },
    ): Promise<void> {
      try {
        const place = ev.placePrediction.toPlace();
        await place.fetchFields({
          fields: ["formattedAddress", "location", "displayName"],
        });
        const address =
          place.formattedAddress ?? (place.displayName as unknown as string) ?? "";
        const loc = place.location;
        onChangeRef.current(address);
        onPlaceRef.current({
          address,
          location: loc ? { lat: loc.lat(), lng: loc.lng() } : null,
        });
      } catch (err) {
        // Network or quota error; fall back silently — user can still type.
        console.warn("Place fetch failed:", err);
      }
    }

    element.addEventListener("gmp-select", handleSelect);
    containerRef.current.appendChild(element);

    return () => {
      element.removeEventListener(
        "gmp-select",
        handleSelect as unknown as EventListener,
      );
      element.remove();
    };
  }, [placesLibrary, id, placeholder, country]);

  return <div ref={containerRef} className="place-autocomplete-host w-full" />;
}
