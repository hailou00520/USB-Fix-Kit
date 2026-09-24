import * as React from "react"
import * as ScrollAreaPrimitive from "@radix-ui/react-scroll-area"
import { cn } from "@/lib/utils"

function ScrollArea({
  className,
  children,
  /** 右侧留独立滚轮轨，避免叠在卡片上；可向右借入父级 padding */
  rail = false,
  /** 仅长列表/日志需要常显；其它区域保持 hover 才出现，避免满屏丑条 */
  type = "hover",
  ...props
}: React.ComponentProps<typeof ScrollAreaPrimitive.Root> & {
  rail?: boolean
}) {
  return (
    <ScrollAreaPrimitive.Root
      data-slot="scroll-area"
      data-rail={rail || undefined}
      data-type={type}
      type={type}
      className={cn("relative overflow-hidden", rail && "scroll-rail", className)}
      {...props}
    >
      <ScrollAreaPrimitive.Viewport
        data-slot="scroll-area-viewport"
        className="focus-visible:ring-ring/50 size-full rounded-[inherit] transition-[color,box-shadow] outline-none focus-visible:ring-[3px] focus-visible:outline-1"
      >
        {children}
      </ScrollAreaPrimitive.Viewport>
      <ScrollBar always={type === "always"} />
      <ScrollAreaPrimitive.Corner />
    </ScrollAreaPrimitive.Root>
  )
}

function ScrollBar({
  className,
  orientation = "vertical",
  always = false,
  ...props
}: React.ComponentProps<typeof ScrollAreaPrimitive.ScrollAreaScrollbar> & {
  always?: boolean
}) {
  return (
    <ScrollAreaPrimitive.ScrollAreaScrollbar
      data-slot="scroll-area-scrollbar"
      orientation={orientation}
      className={cn(
        "flex touch-none transition-colors select-none",
        orientation === "vertical" &&
          cn(
            "absolute inset-y-1 right-0.5 z-10 flex justify-center",
            always ? "w-2 p-px" : "w-1.5"
          ),
        orientation === "horizontal" &&
          cn(
            "flex-col border-t border-t-transparent p-px",
            always ? "h-2" : "h-1.5"
          ),
        className
      )}
      {...props}
    >
      <ScrollAreaPrimitive.ScrollAreaThumb
        data-slot="scroll-area-thumb"
        className={cn(
          "relative flex-1 rounded-full",
          always
            ? "bg-teal-700/40 hover:bg-teal-700/60"
            : "bg-primary/30 hover:bg-primary/50"
        )}
      />
    </ScrollAreaPrimitive.ScrollAreaScrollbar>
  )
}

export { ScrollArea, ScrollBar }
