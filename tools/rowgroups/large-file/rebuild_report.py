"""Rebuild the minimal request viewer from saved trace JSON; no engines needed."""
import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parents[2] / 'Plank.Tool' / 'Querying'))
from network import write_report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, default=ROOT / 'network-profile.json')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    write_report(json.loads(args.profile.read_text()), args.output)
    print(args.output.resolve())


if __name__ == '__main__':
    main()
