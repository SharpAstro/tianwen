# Stacking and processing app

**Objective:** A stand-alone app to calibrate, stack and post-process astro images, in the spirit of PixInsight; its live stacking view is the one piece the tianwen GUI would embed.

## Stack

**Objective:** Shows the session's frames with their quality (FWHM, stars, weight), which frames are left out and why, and the pipeline settings that turn them into one master.

```screen id=home
Stack workspace: frames, pipeline, preview
```

**Notes:** One workspace switch (Stack · Process · Live) in the header instead of a menu tree. Frames over 2σ on FWHM are unticked automatically and the reason is said in words, not only by the amber colour. The image canvas is a placeholder.

## Process

**Objective:** Shows post-processing of the master: a process list on the left, the chosen process's settings and the history on the right, a before/after preview in the middle.

```screen id=process
Process workspace: hyperbolic stretch on the master
```

**Notes:** One process open at a time, docked, instead of PixInsight's floating windows. History is a plain list; clicking a step would roll the preview back to it.

## Live

**Objective:** Shows a live stack growing during a session: the stack so far, the frame being exposed, and which frames were kept or rejected.

```screen id=live
Live stack: the view the tianwen GUI would embed
```

**Notes:** This is the screen to share with the tianwen GUI; its right panel and frame strip should work on their own without the Stack and Process workspaces.
