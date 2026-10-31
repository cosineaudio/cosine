/**
 * Centre `el` inside `container` by scrolling the container itself.
 *
 * Deliberately not `el.scrollIntoView()`: that walks up and scrolls *every* scrollable ancestor,
 * and `overflow: hidden` boxes are still programmatically scrollable. Inside the full-screen
 * player that dragged the dialog root itself - the header slid off the top and the bottom-anchored
 * visualizer rode up with it, leaving a band of bare background along the bottom edge.
 *
 * Offsets come from rects rather than `offsetTop` so the result doesn't depend on which ancestor
 * happens to be the element's `offsetParent`.
 */
export function scrollCentre(
  container: HTMLElement,
  el: HTMLElement,
  behavior: ScrollBehavior = 'auto',
): void {
  const delta = el.getBoundingClientRect().top - container.getBoundingClientRect().top;
  const top = container.scrollTop + delta - (container.clientHeight - el.clientHeight) / 2;
  container.scrollTo({ top, behavior });
}
