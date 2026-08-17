#!/usr/bin/env python3
"""Publish per-machine benchmark snapshots as a CPU-selectable matrix.

The suite measures one machine per run. Comparing libraries on a single CPU hides
the cases where the ranking depends on the machine — vector width, core count and
memory bandwidth all move the results — so the published matrix carries one
snapshot per CPU and lets the reader pick.

Each machine keeps its own pair of files rather than being merged into one, so the
homepage downloads a single CPU's results instead of every CPU's.

Usage:
  publish_matrix.py --machine <id>:<label>:<write.json>:<read.json> [...] --default <id>
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
OUTPUT_DIRECTORY = REPOSITORY_ROOT / "docs" / "benchmarks"
INDEX_NAME = "machines-v1.json"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--machine", action="append", required=True, metavar="SPEC",
                        help="<id>:<label>:<write.json>:<read.json>, repeatable.")
    parser.add_argument("--default", required=True, metavar="ID",
                        help="Machine shown before the reader picks one.")
    parser.add_argument("--output", type=Path, default=OUTPUT_DIRECTORY,
                        help=f"Directory to write into (default: {OUTPUT_DIRECTORY}).")
    return parser.parse_args()


def parse_machine(spec: str) -> tuple[str, str, Path, Path]:
    parts = spec.split(":")
    if len(parts) != 4:
        raise SystemExit(f"--machine needs <id>:<label>:<write.json>:<read.json>, got '{spec}'")
    identifier, label, write_path, read_path = parts
    return identifier, label, Path(write_path), Path(read_path)


def load_snapshot(path: Path) -> dict:
    if not path.is_file():
        raise SystemExit(f"missing snapshot: {path}")
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def describe(snapshot: dict) -> dict:
    """The bits of a snapshot the picker needs before the snapshot itself is fetched."""
    environment = snapshot["environment"]
    return {
        "cpu": environment["cpu"],
        "logicalProcessors": environment["logicalProcessors"],
        "operatingSystem": environment["operatingSystem"],
        "dotNetVersion": environment["dotNetVersion"],
        "commit": environment["commit"],
    }


def main() -> int:
    args = parse_args()
    output = args.output
    output.mkdir(parents=True, exist_ok=True)

    machines = []
    identifiers = set()
    for spec in args.machine:
        identifier, label, write_path, read_path = parse_machine(spec)
        if identifier in identifiers:
            raise SystemExit(f"duplicate machine id '{identifier}'")
        identifiers.add(identifier)

        write_snapshot = load_snapshot(write_path)
        read_snapshot = load_snapshot(read_path)
        write_environment = describe(write_snapshot)
        read_environment = describe(read_snapshot)

        # A machine whose two halves came from different builds cannot be compared
        # against itself, let alone against another machine.
        if write_environment["commit"] != read_environment["commit"]:
            raise SystemExit(
                f"{identifier}: write snapshot is at {write_environment['commit']} but read snapshot "
                f"is at {read_environment['commit']}")
        if write_environment["cpu"] != read_environment["cpu"]:
            raise SystemExit(
                f"{identifier}: write snapshot ran on {write_environment['cpu']} but read snapshot "
                f"ran on {read_environment['cpu']}")

        write_name = f"write-{identifier}-v1.json"
        read_name = f"read-{identifier}-v1.json"
        shutil.copyfile(write_path, output / write_name)
        shutil.copyfile(read_path, output / read_name)

        machines.append({
            "id": identifier,
            "label": label,
            "environment": write_environment,
            "generatedAt": write_snapshot["generatedAt"],
            "write": write_name,
            "read": read_name,
        })
        print(f"  {identifier:<18} {write_environment['cpu']} "
              f"({write_environment['logicalProcessors']} logical) -> {write_name}, {read_name}")

    if args.default not in identifiers:
        raise SystemExit(f"--default '{args.default}' is not one of {sorted(identifiers)}")

    commits = {machine["environment"]["commit"] for machine in machines}
    if len(commits) > 1:
        print(f"  WARNING: machines are not all at the same commit: {sorted(commits)}", file=sys.stderr)

    index = {"schemaVersion": 1, "defaultMachine": args.default, "machines": machines}
    index_path = output / INDEX_NAME
    with index_path.open("w", encoding="utf-8") as handle:
        json.dump(index, handle, indent=2)
        handle.write("\n")
    print(f"  index -> {index_path.relative_to(REPOSITORY_ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
