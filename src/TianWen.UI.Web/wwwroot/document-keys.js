// Delivers keyboard shortcuts to the canvas app no matter where DOM focus happens to sit.
//
// The canvas IS the app here -- [O], [D], F3, the arrows and every other shortcut are canvas keys --
// but a <canvas> only receives keydown while it holds DOM focus, and it takes focus exactly once
// (AutoFocus, at startup). Planner.RestoreCanvasFocus hands it back after the two ways it normally
// leaves: a chrome button click, and a touch gesture (which cannot restore it by itself, because the
// touch bridge preventDefault()s touchstart and that suppresses focus-on-press along with the page
// scrolling it exists to stop). Neither covers focus taken by something the app never sees -- devtools,
// an alt-tab that returns to <body>, an extension, the browser's find bar. After one of those the map
// still pans and zooms perfectly, because pointer events need no focus, while every key does nothing:
// it reads as "[O] is broken" rather than as "the page cannot hear me".
//
// So the document is a SECOND source for the same keys, and the canvas handler stays the first. That
// ordering is deliberate: if this module fails to import, the app degrades to exactly what it did
// before the module existed (shortcuts work while the canvas is focused) instead of losing the
// keyboard altogether. The cost of the overlap is one guard, below.
//
// What it must NOT take, each a real element on this page rather than a hypothetical:
//   - a real editable: the Lat/Lon number fields, and the CanvasTextOverlay <input> floated over an
//     active canvas text widget for IME / clipboard / the mobile keyboard. While one of those holds
//     focus it owns every key, including the letters that are shortcuts everywhere else.
//   - the canvas itself, whose Blazor @onkeydown already delivered the event -- this would double it.
//   - Tab, the browser's focus navigation. Canvas text fields cycle with Tab too, but only while the
//     overlay <input> holds focus, and that is an editable, so it never reaches here.
//   - Enter and Space ON A BUTTON OR LINK, which are that control's own activation keys: a focused
//     chip would otherwise be clicked AND deliver the key to the map from one press. Letters are safe
//     there (the browser does nothing with a letter on a button), and letters are what the shortcuts are.
//
// Modifier-only keydowns are dropped here rather than in .NET: they map to nothing and arrive in pairs
// around every chord, so filtering them costs one lookup instead of an interop crossing.
//
// No preventDefault anywhere: this READS keys, it does not claim them. Suppressing here would quietly
// eat the browser's own shortcuts for the sake of keys the app mostly ignores.

const MODIFIERS = new Set(["Shift", "Control", "Alt", "Meta", "CapsLock", "NumLock", "ScrollLock"]);
const ACTIVATION = new Set(["Enter", " ", "Spacebar"]);

/**
 * Starts routing document-level keydowns to the component.
 * @param {object} ref DotNetObjectReference to the component exposing HandleDocumentKey.
 * @param {string} canvasId id of the canvas whose own keydown handler is the primary path.
 * @returns {object} a handle whose detach() removes the listener.
 */
export function attach(ref, canvasId) {
  const onKeyDown = (e) => {
    if (MODIFIERS.has(e.key)) return;

    const target = e.target;
    if (!(target instanceof Element)) {
      // <body> or the document itself: nothing owns the key, so the app does.
      invoke(ref, e);
      return;
    }
    if (target.id === canvasId) return; // already delivered by the canvas @onkeydown
    if (target.closest("input, textarea, select, [contenteditable]")) return;
    if (e.key === "Tab") return;
    if (ACTIVATION.has(e.key) && target.closest("button, a, [role=button]")) return;

    invoke(ref, e);
  };

  document.addEventListener("keydown", onKeyDown);
  return {
    detach: () => document.removeEventListener("keydown", onKeyDown)
  };
}

function invoke(ref, e) {
  // Fire-and-forget, like the repaint pump: a throw here is the component having gone away between
  // the keypress and the call, and an unhandled rejection would be the only thing it produced.
  ref.invokeMethodAsync("HandleDocumentKey", e.key, e.shiftKey, e.ctrlKey, e.altKey)
    .catch(() => { /* component gone */ });
}
