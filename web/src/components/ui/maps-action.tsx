import Link from "next/link";
import type { ComponentType } from "react";
import { cn } from "@/lib/utils";

export interface MapsActionProps {
  icon: ComponentType<{ className?: string }>;
  label: string;
  href?: string;
  onClick?: () => void;
  /** The one primary action gets a solid blue circle; the rest are quiet grey. */
  primary?: boolean;
  download?: string;
}

/**
 * The Google Maps place-panel action: a round icon button stacked over a small
 * blue label, laid out in a row (Directions / Save / Nearby / Share). This —
 * not big filled buttons — is what makes a panel read as Maps.
 */
export function MapsAction({
  icon: Icon,
  label,
  href,
  onClick,
  primary,
  download,
}: MapsActionProps): React.JSX.Element {
  const body = (
    <>
      <span
        className={cn(
          "shadow-google flex h-11 w-11 items-center justify-center rounded-full border border-[#dadce0] bg-card transition-colors group-hover/maps-action:bg-[#f8f9fa]",
          primary ? "text-primary" : "text-[#5f6368]",
        )}
      >
        <Icon className="h-[19px] w-[19px]" />
      </span>
      <span className="text-[12px] leading-none font-medium text-[#3c4043]">{label}</span>
    </>
  );

  const className =
    "group/maps-action flex w-16 flex-col items-center gap-1.5 rounded-lg py-1 text-center outline-none focus-visible:ring-2 focus-visible:ring-primary/30";

  if (href) {
    return (
      <Link href={href} className={className} download={download} aria-label={label}>
        {body}
      </Link>
    );
  }
  return (
    <button type="button" onClick={onClick} className={className} aria-label={label}>
      {body}
    </button>
  );
}
