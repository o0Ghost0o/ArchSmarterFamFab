import argparse
import logging
import os
import sys
import time
import json

import numpy as np
import torch
import xatlas
import rembg
from PIL import Image

# Add triposr to path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'triposr'))

from tsr.system import TSR
from tsr.utils import remove_background, resize_foreground
from tsr.bake_texture import bake_texture


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("image", type=str, help="Path to input image.")
    parser.add_argument("--output-dir", default="output", type=str)
    parser.add_argument("--device", default="cpu", type=str)
    parser.add_argument("--pretrained-model", default="stabilityai/TripoSR", type=str)
    parser.add_argument("--chunk-size", default=8192, type=int)
    parser.add_argument("--mc-resolution", default=256, type=int)
    parser.add_argument("--foreground-ratio", default=0.85, type=float)
    parser.add_argument("--bake-texture", action="store_true", default=True)
    parser.add_argument("--texture-resolution", default=2048, type=int)
    parser.add_argument("--model-save-format", default="obj", type=str, choices=["obj", "glb"])
    parser.add_argument("--no-remove-bg", action="store_true")
    args = parser.parse_args()

    os.makedirs(args.output_dir, exist_ok=True)

    device = args.device
    if device.startswith("cuda") and not torch.cuda.is_available():
        device = "cpu"
        logging.warning("CUDA not available, falling back to CPU")

    logging.info(f"Loading TripoSR model on {device}...")
    start = time.time()
    model = TSR.from_pretrained(
        args.pretrained_model,
        config_name="config.yaml",
        weight_name="model.ckpt",
    )
    model.renderer.set_chunk_size(args.chunk_size)
    model.to(device)
    logging.info(f"Model loaded in {time.time() - start:.1f}s")

    # Process image
    if args.no_remove_bg:
        image = np.array(Image.open(args.image).convert("RGB"))
        image = Image.fromarray(image)
    else:
        rembg_session = rembg.new_session()
        image = remove_background(Image.open(args.image), rembg_session)
        image = resize_foreground(image, args.foreground_ratio)
        image = np.array(image).astype(np.float32) / 255.0
        image = image[:, :, :3] * image[:, :, 3:4] + (1 - image[:, :, 3:4]) * 0.5
        image = Image.fromarray((image * 255.0).astype(np.uint8))

    # Run model
    logging.info("Running inference...")
    start = time.time()
    with torch.no_grad():
        scene_codes = model([image], device=device)
    logging.info(f"Inference done in {time.time() - start:.1f}s")

    # Extract mesh
    logging.info("Extracting mesh...")
    start = time.time()
    meshes = model.extract_mesh(scene_codes, not args.bake_texture, resolution=args.mc_resolution)
    logging.info(f"Mesh extracted in {time.time() - start:.1f}s")

    mesh = meshes[0]

    # Export mesh
    out_mesh_path = os.path.join(args.output_dir, f"mesh.{args.model_save_format}")
    
    if args.bake_texture:
        logging.info("Baking texture...")
        start = time.time()
        bake_output = bake_texture(mesh, model, scene_codes[0], args.texture_resolution)
        logging.info(f"Texture baked in {time.time() - start:.1f}s")

        out_texture_path = os.path.join(args.output_dir, "texture.png")
        Image.fromarray((bake_output["colors"] * 255.0).astype(np.uint8)).transpose(Image.FLIP_TOP_BOTTOM).save(out_texture_path)

        xatlas.export(out_mesh_path, mesh.vertices[bake_output["vmapping"]], bake_output["indices"], bake_output["uvs"], mesh.vertex_normals[bake_output["vmapping"]])
        
        result = {
            "mesh": out_mesh_path,
            "texture": out_texture_path,
            "vertices": len(mesh.vertices),
            "faces": len(mesh.faces)
        }
    else:
        mesh.export(out_mesh_path)
        result = {
            "mesh": out_mesh_path,
            "vertices": len(mesh.vertices),
            "faces": len(mesh.faces)
        }

    # Write result JSON
    result_path = os.path.join(args.output_dir, "result.json")
    with open(result_path, "w") as f:
        json.dump(result, f, indent=2)
    
    logging.info(f"Done. Output: {args.output_dir}")
    print(result_path)


if __name__ == "__main__":
    logging.basicConfig(format="%(asctime)s - %(levelname)s - %(message)s", level=logging.INFO)
    main()
