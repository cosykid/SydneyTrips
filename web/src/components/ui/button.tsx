import { Button as ButtonPrimitive } from "@base-ui/react/button"
import { cva, type VariantProps } from "class-variance-authority"

import { cn } from "@/lib/utils"

// Google Maps button language: pill-shaped, Roboto-medium, Material elevation.
// Filled blue for the primary action; white "chip" outlines for everything else.
const buttonVariants = cva(
  "group/button inline-flex shrink-0 items-center justify-center rounded-full border border-transparent bg-clip-padding text-sm font-medium tracking-[0.01em] whitespace-nowrap transition-all outline-none select-none focus-visible:ring-3 focus-visible:ring-ring/40 active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        // Google Maps buttons are white pills — thin #dadce0 border, soft
        // Material shadow, dark grey label, blue *icon* only. (Think the
        // category chips and the map FAB controls.) No solid-blue fills.
        default:
          "border-[#dadce0] bg-card text-[#3c4043] shadow-google hover:bg-[#f8f9fa] hover:shadow-google-md [&_svg]:text-primary",
        outline:
          "border-[#dadce0] bg-card text-[#3c4043] hover:bg-[#f1f3f4] aria-expanded:bg-[#f1f3f4]",
        secondary:
          "bg-secondary text-[#3c4043] hover:bg-[#e8eaed] aria-expanded:bg-[#e8eaed]",
        ghost:
          "text-[#3c4043] hover:bg-secondary hover:text-foreground aria-expanded:bg-secondary",
        // Opt-in solid blue, for the rare true hero action. Used sparingly.
        primary:
          "bg-primary text-primary-foreground hover:bg-[#1b66c9] active:bg-[#185abc]",
        destructive:
          "bg-transparent text-destructive hover:bg-destructive/10 focus-visible:ring-destructive/20",
        link: "text-primary underline-offset-4 hover:underline",
      },
      size: {
        default:
          "h-9 gap-1.5 px-4 has-data-[icon=inline-end]:pr-3 has-data-[icon=inline-start]:pl-3",
        xs: "h-6 gap-1 px-2.5 text-xs has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&_svg:not([class*='size-'])]:size-3",
        sm: "h-8 gap-1.5 px-3 text-[0.8rem] has-data-[icon=inline-end]:pr-2 has-data-[icon=inline-start]:pl-2 [&_svg:not([class*='size-'])]:size-3.5",
        lg: "h-10 gap-2 px-5 has-data-[icon=inline-end]:pr-4 has-data-[icon=inline-start]:pl-4",
        icon: "size-9",
        "icon-xs": "size-6 [&_svg:not([class*='size-'])]:size-3",
        "icon-sm": "size-8",
        "icon-lg": "size-10",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
)

function Button({
  className,
  variant = "default",
  size = "default",
  ...props
}: ButtonPrimitive.Props & VariantProps<typeof buttonVariants>) {
  return (
    <ButtonPrimitive
      data-slot="button"
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  )
}

export { Button, buttonVariants }
