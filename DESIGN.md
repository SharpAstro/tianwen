# Stacking and processing app

**Objective:** A stand-alone app to calibrate, stack and post-process astro images, in the spirit of PixInsight; tianwen's GUI gets live stacking only, as a mode of its frame preview.

## Guided

**Objective:** Shows the pre-built "tianwen viewer" workflow one step at a time, like Astro Pixel Processor: load, calibrate, analyse, register, integrate, plate solve, SPCC, gradient, stretch.

```screen id=guided
Step 6 of 9: plate solve the master
```

**Notes:** The step bar is the workflow itself, laid out flat: done steps carry ✓, the current one is selected, later ones are dim, so the state is in words and marks, not colour alone. Each step has one panel (what it does, its few settings, its result) and Back / Next; the canvas shows that step's output (here the solved sky grid). Plate solve and SPCC use the same code as the tianwen viewer so a master looks the same in both. "Open as workflow" leads to the Workflow editor, where gates and extra steps live: Guided is the easy road, Workflow and Process the flexible ones.

## Stack

**Objective:** Shows the session's frames with their quality (FWHM, stars, weight), which frames are left out and why, and the pipeline settings that turn them into one master.

```screen id=home
Stack workspace: frames, pipeline, preview
```

**Notes:** One workspace switch (Guided · Stack · Process · Workflow) in the header instead of a menu tree. Frames over 2σ on FWHM are unticked automatically and the reason is said in words, not only by the amber colour. The image canvas is a placeholder.

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

## Live stack in the frame preview

**Objective:** Shows live stacking as a mode of tianwen's frame preview during a session: one switch flips the same canvas between the last frame and the running stack.

```screen id=session
Frame mode: the last frame and its numbers
```

```screen id=live
Live stack mode: the running stack, what was left out, save or restart
```

**Notes:** Live stacking is no longer a workspace of its own: it is a Frame / Live stack switch on the preview tianwen already has, with the same stretch control in both modes. The bottom strip changes with the mode: the frame's exposure and quality in Frame, the stack's count, time, frames left out (with the reason) and SNR gain in Live stack. Stacking settings (calibration, rejection) come from the workflow, so the preview has no settings panel. The stand-alone app keeps only Stack · Process · Workflow; the "tianwen preview ↗" link in its header exists only to reach this mock-up.
