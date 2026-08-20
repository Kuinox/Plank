#!/usr/bin/env bash
# Import small files from the apache/parquet-testing submodule into the reader fuzzer's
# seed corpus, as fuzz/reader-corpus/pqt-*.bin.
#
# Why these are worth seeding at all: every other seed in that directory comes out of
# CorpusGenerator, which drives Plank's own writer. That bounds the corpus to what Plank
# can produce -- so DELTA_BYTE_ARRAY as parquet-mr emits it, hadoop-framed LZ4, multi-member
# gzip, FLOAT16, INT96, BSON and JSON annotations, geospatial statistics and the
# plaintext-footer encrypted layout are all unreachable no matter how long the fleet runs,
# because reaching them by mutation means inventing a valid envelope byte for byte.
#
# The output is not checked in. It is a copy of files the submodule already pins by commit,
# so committing it would vendor the corpus twice over and leave the two free to drift.
# deploy.sh updates the submodule and runs this, the same way it regenerates the
# writer-derived seeds; run it by hand to fuzz locally, or after bumping the submodule.
#
# The output is prefixed with selector byte 0x00, which tells PlankReaderFuzzTarget to bind
# the file's own schema. Reading these through a fixed requested schema would skip every
# decoder the file exists to reach.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/third_party/parquet-testing"
CORPUS="$ROOT/fuzz/reader-corpus"

# AFL spends time proportional to input size, and a seed's value here is the envelope it
# carries rather than the rows inside it. Everything above this is a bulk-data file whose
# encodings are already covered by a small sibling -- alltypes_tiny_pages is 454 KB of the
# same types as alltypes_plain at 1.8 KB.
MAX_BYTES="${PLANK_SEED_MAX_BYTES:-8192}"

if [ ! -f "$SOURCE/README.md" ]; then
  echo "third_party/parquet-testing is not checked out." >&2
  echo "Run: git submodule update --init third_party/parquet-testing" >&2
  exit 2
fi

mkdir -p "$CORPUS"
rm -f "$CORPUS"/pqt-*.bin

written=0
skipped=0

# On-disk size is not the only thing that makes a seed expensive. large_string_map.brotli
# is 4.3 KB of brotli that inflates to 2,147,483,827 bytes -- a 496,528x expansion, and the
# only file in the corpus above 720 KB uncompressed. As a seed it would cost every worker
# ~4s and 2 GiB of RSS on every execution of it, which trips AFL's memory limit and starves
# the rest of the corpus. It is a reader stress case, and it is covered as one in
# ParquetTestingCompatibilityTests instead.
BOMBS="data/large_string_map.brotli.parquet"

# shredded_variant/ is left out on purpose: it is 138 files of one schema shape, which would
# treble the seed count while adding one decode path. bad_data/variants covers that path.
while IFS= read -r file; do
  rel="${file#"$SOURCE"/}"

  case " $BOMBS " in
    *" $rel "*) skipped=$((skipped + 1)); continue ;;
  esac

  size=$(stat -c %s "$file")
  if [ "$size" -gt "$MAX_BYTES" ]; then
    skipped=$((skipped + 1))
    continue
  fi

  # data/geospatial/crs-srid.parquet -> pqt-data-geospatial-crs-srid.bin
  name="pqt-$(printf '%s' "${rel%.parquet}" | sed 's/\.parquet\.encrypted$/-encrypted/; s#/#-#g').bin"

  printf '\x00' > "$CORPUS/$name"
  cat "$file" >> "$CORPUS/$name"
  written=$((written + 1))
done < <(find "$SOURCE/data" "$SOURCE/bad_data" \
           \( -name '*.parquet' -o -name '*.parquet.encrypted' \) -type f | sort)

echo "wrote $written seeds to fuzz/reader-corpus (skipped $skipped: over ${MAX_BYTES}B, or a decompression bomb)"
