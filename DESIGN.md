# Stacking and processing app

**Objective:** A stand-alone app to calibrate, stack and post-process (edit) astro images, in the spirit of PixInsight, with non-destructive editing throughout; tianwen's GUI gets live stacking only, as a mode of its frame preview.

**Principle: non-destructive editing.** An image is its original plus a list of edits (the Image class): crop, gradient removal, a stretch are each an edit that can be switched off, changed or reordered later, and later edits re-run. Nothing is written over. When many images are open (five masters, starless and star copies, masks), images not being worked on are kept on disk in a cache folder and load back on demand; the app says which ones and how much memory is in use.

## Guided

**Objective:** Shows the pre-built "tianwen viewer" workflow one step at a time, like Astro Pixel Processor: load, calibrate, analyse, register, integrate, crop, plate solve, SPCC, gradient, stretch.

```screen id=guided
Step 7 of 10: plate solve the cropped master
```

**Notes:** The step bar is the workflow itself, laid out flat: done steps carry ✓, the current one is selected, later ones are dim, so the state is in words and marks, not colour alone. Each step has one panel (what it does, its few settings, its result) and Back / Next; the canvas shows that step's output (here the solved sky grid). Plate solve and SPCC use the same code as the tianwen viewer so a master looks the same in both. "Open as workflow" leads to the Workflow editor, where gates and extra steps live: Guided is the easy road, Workflow and Process the flexible ones. Guided is always one image (SDI); it has no Windows switch. Crop sits before plate solving so the solve and SPCC never see the ragged stacking edges. "Run on until a step needs me" (on by default) carries on through the steps and stops only where one asks for something, such as a failed solve; off, it stops after every step.

## Jump ahead

**Objective:** Shows going straight to a later step, as in APP: click 5 Integrate on a new session and press Integrate; steps 1–4 that are not done, or no longer match their input, run first.

```screen id=integrate
Step 5 chosen directly: what runs first and why
```

**Notes:** Every step keeps what it made together with the inputs it made it from. Picking a later step checks the earlier ones: ✓ still valid and reused, ↻ done but its input changed (here new flats) so it runs again, ○ not done yet so it runs. The marks are in the step bar and spelt out in the panel, and the button says what it will do ("Integrate (runs 2–4 first)"). This is the non-destructive principle applied to stacking: nothing has to be redone by hand, and nothing is redone that is still good.

## Stack

**Objective:** Shows the session's frames with their quality (FWHM, stars, weight), which frames are left out and why, and the workflow that turns them into one master.

```screen id=home
Stack workspace: frames, chosen workflow, preview
```

**Notes:** One workspace switch (Guided · Stack · Process · Workflow) in the header instead of a menu tree. Stack does not have its own pipeline settings any more: it runs a workflow. The panel picks one and lists, in words, what it will do with these frames (the gates already decided), with "Edit workflow ↗" for changing it. Frames over 2σ on FWHM are unticked automatically and the reason is said in words, not only by the amber colour. The image canvas is a placeholder.

## Process

**Objective:** Shows post-processing of the master: a process list on the left, the chosen process's settings and the image's edit list on the right, a before/after preview in the middle.

```screen id=process
Process workspace: hyperbolic stretch on the master
```

**Notes:** One process open at a time, docked, instead of PixInsight's floating windows. The history is the image's edit list, not an undo log: each edit has a tick to switch it off and opens its settings when clicked, and the original stays untouched underneath. A process's button says Keep (add or update this edit), not Apply, because nothing is burnt in. The header's One image / Windows switch picks SDI or MDI; One image is the default.

## Crop

**Objective:** Shows cropping the master: cut off the stacking edges automatically, or set the sides, aspect ratio and north-up rotation by hand, with the frame drawn on the whole image.

```screen id=crop
Process: Crop, auto-cropping the stacking edges
```

**Notes:** Crop is an edit like any other: the crop frame is stored, not the cut pixels, so it can be widened again later. Auto-crop finds the edges one of two ways: from the integration's cover map (where fewer than 90 % of frames overlap), which is exact and the default here; or from the image on the GPU, the quick way the tianwen viewer does it today, which is what Guided uses. "Cover map" in the canvas toolbar shows the map. The side fields show the auto values and can be overwritten; dragging the frame does the same. North-up needs a plate solve and says so. Crop is also step 6 of Guided and a step a workflow can hold.

## Windows (MDI)

**Objective:** Shows the optional MDI mode: several images (master, starless, stars, a mask) open side by side as windows, with the process applying to the active one.

```screen id=mdi
Process in Windows mode: pixel math across four windows
```

**Notes:** MDI earns its keep where a process needs more than one image: pixel math, masks, channel combine. Windows tile by default (Cascade and Tabs on offer) so nothing hides another; zoom and pan can be linked. The active window has a thick blue outline and is named in the panel and the status line, so it is not shown by colour alone. The switch lives in the Process header and is remembered; Stack and Guided stay single-image. "Open images" lists each image with where it is (memory or on disk) and how many edits it carries, above a memory bar; images go to the cache folder on their own when memory runs short, and the window title says "on disk" so a slow first redraw is explained.

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
