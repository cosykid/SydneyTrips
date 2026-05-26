import type { CandidateNode } from "@/lib/api/schema";
import { transitStyle } from "@/lib/map/transit";

interface TransitBadgeProps {
  modality: CandidateNode["modality"] | undefined;
  /** Square edge length in pixels. Defaults to 16 (map markers); use ~14 inline in labels. */
  size?: number;
}

/**
 * A Transport for NSW mode chip — a colour-coded rounded square with the mode's letter
 * (T / B / F / L), matching Sydney transport signage and Google Maps' transit overlay.
 * Renders nothing for hubs with no transit mode.
 */
export function TransitBadge({ modality, size = 16 }: TransitBadgeProps): React.JSX.Element | null {
  const style = transitStyle(modality);
  if (!style) return null;
  return (
    <span
      className="inline-flex items-center justify-center rounded-[4px] font-bold leading-none shadow-sm"
      style={{
        backgroundColor: style.color,
        color: style.textColor,
        width: size,
        height: size,
        fontSize: Math.round(size * 0.66),
      }}
      aria-label={style.name}
      title={style.name}
    >
      {style.mark}
    </span>
  );
}
