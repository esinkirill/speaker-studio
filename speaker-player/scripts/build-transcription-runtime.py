"""Stage an offline Windows x64 MuScriptor-small CPU runtime.

Copy prepared model/source files and installed dependencies, then extract an
official CPU torch wheel. By default the target PC needs the official installed
VC14 x64 redistributable; --vc-runtime selects the app-local profile instead.
This script performs no installation, authentication or model download.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil
from zipfile import ZipFile

SOURCE_REVISION = "7f213afecf23bd6a1b8672aa223690ee9807cefb"
SMALL_SHA256 = "bbd482c786b895cf7d8f44185073d951adae2ebb8a66f82ca84cd1f84569549c"
VC_RUNTIME_FILES = ("msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def ignore(directory: str, names: list[str]) -> list[str]:
    return [name for name in names if name in {"__pycache__", "tests", "test", "site-packages", "idlelib", "tkinter", "turtledemo", "ensurepip"}
            or name.endswith((".pyc", ".pyo"))]


def copy_tree(source: Path, destination: Path) -> None:
    if not source.is_dir():
        raise FileNotFoundError(f"Build dependency is missing: {source}")
    shutil.copytree(source, destination, ignore=ignore)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--python-root", type=Path, required=True)
    parser.add_argument("--site-packages", type=Path, required=True)
    parser.add_argument("--muscriptor-source", type=Path, required=True)
    parser.add_argument("--model-source", type=Path, required=True)
    parser.add_argument("--torch-wheel", type=Path, required=True)
    parser.add_argument("--vc-runtime", type=Path,
                        help="Optional app-local x64 VC14 directory. Default: installed system VC14 x64 redistributable.")
    parser.add_argument("--destination", type=Path, default=Path(__file__).resolve().parent.parent)
    return parser


def main() -> None:
    args = build_parser().parse_args()
    root = args.destination.resolve()
    runtime = root / "runtime" / "transcription"
    model = root / "models" / "muscriptor-small"
    if runtime.exists() or model.exists():
        raise FileExistsError("Runtime/model destination already exists; choose a fresh destination.")
    weights = args.model_source / "model.safetensors"
    config_path = args.model_source / "config.json"
    config = json.loads(config_path.read_text(encoding="utf-8"))
    if (config["dim"], config["num_heads"], config["num_layers"], config["card"]) != (768, 12, 14, 1393):
        raise ValueError("Expected the already verified MuScriptor small configuration.")
    if sha256(weights) != SMALL_SHA256:
        raise ValueError("Small checkpoint differs from the locally verified source.")
    if "torch-2.8.0+cpu-cp312-cp312-win_amd64" not in args.torch_wheel.name:
        raise ValueError("Expected official torch 2.8.0+cpu for Python 3.12 / Windows x64.")
    runtime.mkdir(parents=True)
    for name in ["python.exe", "python3.dll", "python312.dll", "LICENSE.txt"]:
        shutil.copy2(args.python_root / name, runtime / name)
    # Default profile uses the target PC's installed VC14 x64 redistributable.
    # The explicit app-local profile keeps the previous DLL/notice copies.
    if args.vc_runtime is not None:
        for name in VC_RUNTIME_FILES:
            shutil.copy2(args.vc_runtime / name, runtime / name)
        licenses = Path(__file__).resolve().parent.parent / "licenses"
        for name in ["Microsoft-VC-Runtime-LICENSE.txt", "Microsoft-VC-Runtime-NOTICE.txt"]:
            shutil.copy2(licenses / name, runtime / name)
    copy_tree(args.python_root / "Lib", runtime / "Lib")
    copy_tree(args.python_root / "DLLs", runtime / "DLLs")
    (runtime / "python312._pth").write_text(".\nLib\nDLLs\nsite-packages\nimport site\n", encoding="ascii")
    packages = runtime / "site-packages"
    packages.mkdir()
    modules = ["numpy", "numpy.libs", "einops", "mido", "packaging", "safetensors", "filelock",
               "sympy", "mpmath", "networkx", "jinja2", "markupsafe", "fsspec", "huggingface_hub",
               "requests", "urllib3", "idna", "charset_normalizer", "certifi", "tqdm", "yaml", "_yaml"]
    distributions = {name.replace(".libs", "").lower() for name in modules} | {"typing_extensions", "pyyaml"}
    for name in modules:
        copy_tree(args.site_packages / name, packages / name)
    shutil.copy2(args.site_packages / "typing_extensions.py", packages / "typing_extensions.py")
    for metadata in args.site_packages.glob("*.dist-info"):
        distribution = metadata.name.split("-", 1)[0].replace("-", "_").lower()
        if distribution in distributions:
            copy_tree(metadata, packages / metadata.name)
    copy_tree(args.muscriptor_source / "muscriptor", packages / "muscriptor")
    # Headers and static .lib files are development artifacts; the CPU model
    # only loads Python modules/.pyd files and their dynamic .dll dependencies.
    torch_pruned_bytes = 0
    with ZipFile(args.torch_wheel) as wheel:
        for member in wheel.infolist():
            relative = Path(member.filename)
            if relative.is_absolute() or ".." in relative.parts:
                raise ValueError("Unsafe wheel member path.")
            prune = (member.filename.startswith(("torch/include/", "torch/share/", "torch/bin/"))
                     or relative.suffix.lower() in {".lib", ".pdb"}
                     or "test" in relative.parts or "tests" in relative.parts)
            if prune:
                torch_pruned_bytes += member.file_size
                continue
            if member.is_dir():
                continue
            destination = packages / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            with wheel.open(member) as source, destination.open("wb") as target:
                shutil.copyfileobj(source, target)
    model.mkdir(parents=True)
    shutil.copy2(weights, model / "model.safetensors")
    shutil.copy2(config_path, model / "config.json")
    (packages / "muscriptor" / "SOURCE_REVISION.txt").write_text(SOURCE_REVISION + "\n", encoding="ascii")
    entries = []
    for directory in (runtime, model):
        for file in sorted(directory.rglob("*")):
            if file.is_file():
                entries.append({"path": file.relative_to(root).as_posix(), "bytes": file.stat().st_size, "sha256": sha256(file)})
    report = {"format": "speaker-transcription-runtime-manifest-v1", "sourceRevision": SOURCE_REVISION,
              "profile": "app-local-vc14" if args.vc_runtime is not None else "system-vc14",
              "prerequisites": [] if args.vc_runtime is not None else [
                  {"component": "Microsoft Visual C++ Redistributable v14", "architecture": "x64", "deployment": "system"}],
              "pythonVersion": "3.12.14", "torchVersion": "2.8.0+cpu", "device": "cpu",
              "model": "MuScriptor small", "modelSha256": SMALL_SHA256,
              "modelLicense": "CC BY-NC 4.0", "modelLicenseUrl": "https://creativecommons.org/licenses/by-nc/4.0/",
              "sourceLicense": "MIT", "sourceUrl": "https://github.com/muscriptor/muscriptor",
              "torchWheelSha256": sha256(args.torch_wheel), "torchDevelopmentBytesExcluded": torch_pruned_bytes,
              "msvcRuntimeRequiredFiles": list(VC_RUNTIME_FILES),
              "msvcRuntimeFiles": list(VC_RUNTIME_FILES) if args.vc_runtime is not None else [],
              "msvcRuntimeDeployment": "app-local" if args.vc_runtime is not None else "system",
              "msvcRuntimeLicense": "Microsoft-VC-Runtime-LICENSE.txt" if args.vc_runtime is not None else None,
              "runtimeBytes": sum(e["bytes"] for e in entries if e["path"].startswith("runtime/")),
              "modelBytes": sum(e["bytes"] for e in entries if e["path"].startswith("models/")),
              "totalBytes": sum(e["bytes"] for e in entries), "fileCount": len(entries), "entries": entries}
    destination = runtime / "runtime-manifest.json"
    destination.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "entries"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
