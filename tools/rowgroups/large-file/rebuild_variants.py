"""Generate layouts from aligned data, optionally restoring the saved parts."""
import argparse
import hashlib
import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent))
from stream_repack import OffsetPages

TARGETS = [1000, 10000, 100000, 200000, 500000, 1000000, 2000000, 5000000, 10000000]


def restore(parts, output, manifest):
    if output.exists():
        raise FileExistsError(output)
    temporary = output.with_name(output.name + '.incomplete')
    whole = hashlib.sha256()
    with temporary.open('xb') as dst:
        for part in manifest['parts']:
            path = parts / part['file_name']
            digest, size = hashlib.sha256(), 0
            with path.open('rb') as src:
                while chunk := src.read(8 * 1024 * 1024):
                    digest.update(chunk)
                    whole.update(chunk)
                    dst.write(chunk)
                    size += len(chunk)
            if size != part['bytes'] or digest.hexdigest() != part['sha256']:
                raise ValueError(f'Part checksum or length mismatch: {path}')
            print(f"Verified part {part['number'] + 1}/{len(manifest['parts'])}", flush=True)
    if temporary.stat().st_size != manifest['file_bytes'] or whole.hexdigest() != manifest['sha256']:
        raise ValueError('Whole backing-file checksum mismatch')
    temporary.rename(output)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    origin = parser.add_mutually_exclusive_group(required=True)
    origin.add_argument('--source', type=Path, help='Existing aligned Parquet')
    origin.add_argument('--parts', type=Path, help='Directory containing the 23 saved parts')
    parser.add_argument('--manifest', type=Path, default=ROOT / 'shared-data.json')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--restore-only', action='store_true', help='Verify and assemble parts without generating variants')
    parser.add_argument('--rows', type=int, nargs='+', choices=TARGETS, default=TARGETS)
    args = parser.parse_args()
    if args.restore_only and not args.parts:
        parser.error('--restore-only requires --parts')
    args.output.mkdir(parents=True, exist_ok=True)
    for target in ([] if args.restore_only else args.rows):
        for path in (args.output / f'hits-rows-{target}.parquet', args.output / f'hits-rows-{target}.json'):
            if path.exists():
                raise FileExistsError(path)
    source = args.source or restore(args.parts, args.output / 'hits-aligned.parquet',
                                    json.loads(args.manifest.read_text()))
    if args.restore_only:
        print(source.resolve())
        return
    pages = OffsetPages(source)
    for target in args.rows:
        info = pages.write(args.output / f'hits-rows-{target}.parquet', target)
        if info['row_group_count'] != math.ceil(pages.rows / target):
            raise ValueError('Unexpected row group count')
        (args.output / f'hits-rows-{target}.json').write_text(json.dumps(info, indent=2) + '\n')
        print(json.dumps(info), flush=True)


if __name__ == '__main__':
    main()
