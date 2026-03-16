/**
 * Dashboard responsive utilities
 * Maps backend LayoutItem breakpoints to frontend CSS classes
 */

export interface LayoutItem {
  id: string
  x: number
  y: number
  w: number
  h: number
  lg?: number
  md?: number
  sm?: number
  xs?: number
}

export type DeviceType = 'lg' | 'md' | 'sm' | 'xs'

// Element Plus col span mapping
export const deviceBreakpoints: Record<DeviceType, { min: number; max: number; defaultSpan: number }> = {
  lg: { min: 1200, max: Infinity, defaultSpan: 3 },   // Desktop
  md: { min: 992, max: 1199, defaultSpan: 4 },         // Tablet landscape
  sm: { min: 768, max: 991, defaultSpan: 6 },           // Tablet portrait
  xs: { min: 0, max: 767, defaultSpan: 24 }            // Mobile
}

/**
 * Get the appropriate span for a layout item based on device type
 * Priority: explicit breakpoint value > fallback to w > default
 */
export function getSpanForDevice(item: LayoutItem, device: DeviceType): number {
  const breakpointValue = item[device]
  if (breakpointValue !== undefined && breakpointValue !== null) {
    return breakpointValue
  }
  // Fallback to w (original width)
  if (item.w) {
    return item.w
  }
  // Fallback to device default
  return deviceBreakpoints[device].defaultSpan
}

/**
 * Generate Element Plus col props for a layout item
 */
export function getColProps(item: LayoutItem): Record<string, number> {
  return {
    span: getSpanForDevice(item, 'lg'),
    lg: item.lg ?? getSpanForDevice(item, 'lg'),
    md: item.md ?? getSpanForDevice(item, 'md'),
    sm: item.sm ?? getSpanForDevice(item, 'sm'),
    xs: item.xs ?? getSpanForDevice(item, 'xs')
  }
}

/**
 * Get current device type based on window width
 */
export function getCurrentDeviceType(): DeviceType {
  const width = window.innerWidth
  if (width >= 1200) return 'lg'
  if (width >= 992) return 'md'
  if (width >= 768) return 'sm'
  return 'xs'
}

/**
 * Responsive preview device widths (for editor simulation)
 */
export const previewDeviceWidths: Record<DeviceType, number> = {
  lg: 1920,   // Desktop
  md: 1024,   // Tablet landscape
  sm: 768,    // Tablet portrait
  xs: 375     // Mobile
}

/**
 * Get preview device label
 */
export function getDeviceLabel(device: DeviceType): string {
  const labels: Record<DeviceType, string> = {
    lg: '桌面 Desktop',
    md: '平板 Tablet',
    sm: '小平板 Small Tablet',
    xs: '手機 Mobile'
  }
  return labels[device]
}
