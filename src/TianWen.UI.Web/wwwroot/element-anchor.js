// Where a DOM control sits over the canvas, for a popover the canvas paints under it.
//
// The night calendar is drawn ON the canvas, but the control that opens it is an HTML button in the
// toolbar above. The page used to assume that button was centred over the canvas and centre the calendar
// accordingly; the button then moved (the toolbar is a flex row, and the verdict beside the label changes
// its width), and the calendar opened hundreds of pixels away from it. Measuring the button is the one
// answer that stays right whatever the toolbar's layout does at a given width.

/**
 * The horizontal centre of the first element matching `selector`, in CSS pixels from the canvas's left
 * edge, or null when either is missing or not laid out.
 * @param {string} selector CSS selector of the control, e.g. "[data-night]".
 * @param {string} canvasId The canvas element's id.
 * @returns {number | null}
 */
export function centreXOverCanvas(selector, canvasId) {
  const element = document.querySelector(selector);
  const canvas = document.getElementById(canvasId);
  if (!element || !canvas) {
    return null;
  }
  const e = element.getBoundingClientRect();
  const c = canvas.getBoundingClientRect();
  if (e.width === 0 || c.width === 0) {
    return null;
  }
  return e.left + e.width / 2 - c.left;
}
