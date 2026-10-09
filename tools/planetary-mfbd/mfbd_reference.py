"""
Glue only (docs/plans/planetary-restoration.md, R8 follow-up 4 part 3, #1140): runs torchmfbd (Asensio Ramos, MIT) on the cube
`tianwen planetary lucky-frames` wrote, and writes its object and its PSFs back for `tianwen planetary score`. Every number is read by
tianwen, never here: this file only moves arrays in and out of the outside tool.

Run it from a venv that has torchmfbd beside the system's PyTorch (`python -m venv --system-site-packages`, then `pip install torchmfbd`):

    python mfbd_reference.py <stem>        # reads <stem>.frames.fits, writes <stem>.mfbd.fits and <stem>.mfbd-psf.fits

The configuration is the part pre-registered (the Newtonian, 44 Karhunen-Loeve modes, the object by its Wiener solution, Adam over 50
iterations, one patch); every key it does not name is torchmfbd's own example's (examples/spot_8542/kl.yaml).
"""

import argparse
import os
import tempfile

import numpy as np
import torch
import torchmfbd
import yaml
from astropy.io import fits


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[1])
    parser.add_argument("stem")
    parser.add_argument("--diameter-cm", type=float, default=25.4)
    parser.add_argument("--obscuration-cm", type=float, default=5.8)
    parser.add_argument("--wavelength-a", type=float, default=6500.0)
    parser.add_argument("--modes", type=int, default=44)
    parser.add_argument("--iterations", type=int, default=50)
    parser.add_argument("--optimizer", default="adam", help="adam (pre-registered) or lbfgs (torchmfbd's own example)")
    parser.add_argument("--suffix", default="", help="appended to the outputs' names, so a second run does not overwrite the first")
    args = parser.parse_args()

    with fits.open(args.stem + ".frames.fits") as f:
        cube = f[0].data.astype("float32")
        pix = float(f[0].header["PIXSCALE"])
    n_frames, n, _ = cube.shape

    config = {
        "telescope": {"diameter": args.diameter_cm, "central_obscuration": args.obscuration_cm, "spider": 0},
        "images": {"n_pixel": n, "pix_size": pix, "apodization_border": 10, "remove_gradient_apodization": False},
        "object1": {"wavelength": args.wavelength_a, "image_filter": "scharmer", "cutoff": [0.95, 0.99], "s_u_joint": 250.0},
        "optimization": {"gpu": 0, "transform": "softplus", "softplus_scale": 1.0, "lr_obj": 0.0, "lr_modes": 0.08, "lr_prior": 0.08,
                         "loss_type": "marginal", "stop_psd_optimization": 5, "show_object_info": False},
        "regularization": {"iuwt1": {"variable": "object", "lambda": 0.0, "nbands": 5}},
        "psf": {"model": "kl", "nmax_modes": args.modes, "jitter": "none"},
        "initialization": {"object": "contrast", "modes_std": 0.0},
        "annealing": {"type": "linear", "start_pct": 0.1, "end_pct": 0.6},
        "psd": {"K": 250.0, "v0": 0.1, "p": 2.0},
    }

    # Each frame over its own mean, as torchmfbd's examples feed it; the score normalises the object to its disk again.
    frames = cube / cube.mean(axis=(-1, -2), keepdims=True)
    frames = torch.tensor(frames[None, :, :, :])  # one sequence: (1, n_frames, n, n)

    with tempfile.TemporaryDirectory() as work:
        path = os.path.join(work, "mfbd.yaml")
        with open(path, "w") as f:
            yaml.safe_dump(config, f)
        cwd = os.getcwd()
        os.chdir(work)  # torchmfbd caches its modal basis under ./basis
        try:
            deconv = torchmfbd.Deconvolution(path)
            deconv.add_frames(frames, id_object=0, id_diversity=0, diversity=0.0)
            deconv.deconvolve(infer_object=False, optimizer=args.optimizer, simultaneous_sequences=1, n_iterations=args.iterations)
            device = deconv.modes.device if torch.is_tensor(deconv.modes) else "cpu"
            diversity = [d.to(device) for d in deconv.diversity]
            psf, _ = deconv.compute_psfs(deconv.modes.to(device), diversity)
        finally:
            os.chdir(cwd)

    obj = deconv.obj_diffraction[0][0].cpu().numpy().astype("float32")
    fits.writeto(args.stem + f".mfbd{args.suffix}.fits", obj, overwrite=True)
    psfs = np.fft.fftshift(psf[0][0].detach().cpu().numpy(), axes=(-2, -1)).astype("float32")
    fits.writeto(args.stem + f".mfbd{args.suffix}-psf.fits", psfs, overwrite=True)
    print(f"{args.stem}: {n_frames} frames of {n} px, {args.modes} modes, {args.optimizer} over {args.iterations} iterations; "
          f"wrote {args.stem}.mfbd{args.suffix}.fits and {args.stem}.mfbd{args.suffix}-psf.fits ({psfs.shape[0]} PSFs)")


if __name__ == "__main__":
    main()
