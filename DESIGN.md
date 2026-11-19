# Stacking and processing app

**Objective:** A stand-alone app to calibrate, stack and post-process astro images, in the spirit of PixInsight; its live stacking view is the one piece the tianwen GUI would embed.

## Stack

**Objective:** Shows the session's frames with their quality (FWHM, stars, weight), which frames are left out and why, and the pipeline settings that turn them into one master.

```screen id=home
Stack workspace: frames, pipeline, preview
```

**Notes:** One workspace switch (Stack · Process · Workflow · Live) in the header instead of a menu tree. Frames over 2σ on FWHM are unticked automatically and the reason is said in words, not only by the amber colour. The image canvas is a placeholder.

## Process

**Objective:** Shows post-processing of the master: a process list on the left, the chosen process's settings and the history on the right, a before/after preview in the middle.

```screen id=process
Process workspace: hyperbolic stretch on the master
```

**Notes:** One process open at a time, docked, instead of PixInsight's floating windows. History is a plain list; clicking a step would roll the preview back to it.

## Workflow

**Objective:** Shows a workflow: process steps run in order, with gates (if mono, if luminance present, if more than 60 frames) that pick a branch from the session's own data.

```screen id=workflow
Workflow editor: Deep sky (auto), gate 3 selected
```

**Notes:** A simple workflow is just a list of process steps; gates are the optional extra. Each gate tests one fact of the session (sensor, filters present, frame count, image scale, focal length) and has a Yes and a No branch. The path this session would take is highlighted and marked ✓, so a dry run answers "what will it do with my data" before anything runs. Gates are outlined in amber and start with ◆, so shape and words carry the meaning, not colour alone.

## Live

**Objective:** Shows a live stack growing during a session: the stack so far, the frame being exposed, and which frames were kept or rejected.

```screen id=live
Live stack: the view the tianwen GUI would embed
```

**Notes:** This is the screen to share with the tianwen GUI; its right panel and frame strip should work on their own without the Stack and Process workspaces.
