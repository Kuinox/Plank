#!/usr/bin/env bash
set -euo pipefail

ROOT="/home/kuinox/dev/Plank"
cd "$ROOT"

REPORT_MD="$ROOT/benchmarks/report/DICTIONARY_LAB_EXPLORATION.md"
REPORT_DOT="$ROOT/benchmarks/report/DICTIONARY_LAB_EXPLORATION.dot"
STRING_CATALOG="$ROOT/Plank.DictionaryLab/DictionaryNodeCatalog.cs"
UTF8_CATALOG="$ROOT/Plank.DictionaryLab/Utf8DictionaryNodeCatalog.cs"
ARTIFACT_DIR="$ROOT/BenchmarkDotNet.Artifacts"
mkdir -p "$ARTIFACT_DIR"

start_epoch=$(date +%s)
end_epoch=$((start_epoch + 3600))
cycle=0

insert_node_block() {
  local file="$1"
  local id="$2"
  local parent="$3"
  local notes="$4"
  local ctor="$5"

  perl -0777 -i -pe 's@\n        new\(\n            Id: "tree\.sorted-array\.v1",@\n        new(\n            Id: "'"$id"'",\n            BaseId: "hashtable",\n            ParentId: "'"$parent"'",\n            Notes: "'"$notes"'",\n            Create: static () => new '"$ctor"'()),\n        new(\n            Id: "tree.sorted-array.v1",@s' "$file"
}

refresh_report() {
  local state_path="$1"
  local today
  today=$(date +%F)

  local leaderboard
  leaderboard=$(jq -r '
    .Candidates
    | map(select(.Samples > 0))
    | sort_by(-.MeanMergedSpeedup, -.Samples)
    | .[:20]
    | to_entries
    | .[]
    | "| \(.key + 1) | `\(.value.NodeId)` | \(.value.MeanMergedSpeedup|tostring)x | \(.value.MeanStringSpeedup|tostring)x | \(.value.MeanUtf8Speedup|tostring)x | \(.value.Samples) |"' "$state_path")

  local top5
  top5=$(jq -r '
    .Candidates
    | map(select(.Samples > 0))
    | sort_by(-.MeanMergedSpeedup, -.Samples)
    | .[:5]
    | to_entries
    | .[]
    | "\(.key + 1). \(.value.NodeId) - merged=\(.value.MeanMergedSpeedup|tostring)x samples=\(.value.Samples)"' "$state_path")

  cat > "$REPORT_MD" <<EOM
# Dictionary Lab Exploration

State source: \`$state_path\`  
Last update: $today

## Current Leaderboard

| Rank | Node | Mean merged speedup vs .NET | Mean string speedup | Mean utf8 speedup | Samples |
|---|---|---:|---:|---:|---:|
$leaderboard

## Node Approach Summary

- Top branch expansion per cycle: clone candidate branch rooted at \`hash.linear.tagged.sparse.v1\` using tagged sparse probing behavior.
- Promising non-top expansion per cycle: clone candidate branch rooted at \`hash.robinhood.tagged.sparse.v1\` using robin-hood tagged sparse behavior.
- Node IDs are cycle-stamped and parent-linked in catalogs to preserve branch ancestry.

## Exploration Graph (Graphviz DOT)

Diagram source: [DICTIONARY_LAB_EXPLORATION.dot](/home/kuinox/dev/Plank/benchmarks/report/DICTIONARY_LAB_EXPLORATION.dot)

## Latest Top 5

$top5
EOM

  {
    echo "digraph DictionaryLabExploration {"
    echo "  rankdir=LR;"
    echo "  labelloc=\"t\";"
    echo "  label=\"Dictionary Lab Exploration (Latest Cycle)\";"
    echo "  node [shape=box];"
    echo "  base_hashtable [label=\"base.hashtable\"];"
    echo "  base_btree [label=\"base.btree\"];"
    echo "  hash_linear_v1 [label=\"hash.linear.v1\\nroot\\nhashtable\"];"
    echo "  tree_sorted_array_v1 [label=\"tree.sorted-array.v1\\nroot\\nbtree\"];"
    echo "  base_hashtable -> hash_linear_v1 [label=\"base -> root\"];"
    echo "  base_btree -> tree_sorted_array_v1 [label=\"base -> root\"];"

    jq -r '
      .Candidates
      | map(select(.Samples > 0))
      | sort_by(-.MeanMergedSpeedup, -.Samples)
      | .[:10]
      | .[]
      | "  \(.NodeId | gsub("[^A-Za-z0-9_]"; "_")) [label=\"\(.NodeId)\\n\(.MeanMergedSpeedup|tostring)x merged\\n\(.Samples) samples\"];"' "$state_path"

    jq -r '
      .Candidates
      | map(select(.Samples > 0))
      | sort_by(-.MeanMergedSpeedup, -.Samples)
      | .[:10]
      | .[]
      | select(.NodeId | test("\\.cycle[0-9]+\\.v1$"))
      | .NodeId as $id
      | ($id | capture("(?<p>.*)\\.cycle[0-9]+\\.v1$").p) as $parent
      | "  \($parent | gsub("[^A-Za-z0-9_]"; "_")) -> \($id | gsub("[^A-Za-z0-9_]"; "_")) [label=\"cycle branch\"];"' "$state_path"

    echo "}"
  } > "$REPORT_DOT"

  if ! rg -q "base_hashtable \\[label=\"base.hashtable\"\\];" "$REPORT_DOT"; then
    echo "ERROR: DOT missing base.hashtable vertex." >&2
    return 1
  fi

  if ! rg -q "base_btree \\[label=\"base.btree\"\\];" "$REPORT_DOT"; then
    echo "ERROR: DOT missing base.btree vertex." >&2
    return 1
  fi

  if ! rg -q "base_hashtable -> hash_linear_v1" "$REPORT_DOT"; then
    echo "ERROR: DOT missing base.hashtable -> root edge." >&2
    return 1
  fi

  if ! rg -q "base_btree -> tree_sorted_array_v1" "$REPORT_DOT"; then
    echo "ERROR: DOT missing base.btree -> root edge." >&2
    return 1
  fi
}

latest_state=""
while [ "$(date +%s)" -lt "$end_epoch" ]; do
  cycle=$((cycle + 1))

  top_id="hash.linear.tagged.sparse.cycle${cycle}.v1"
  top_parent="hash.linear.tagged.sparse.v1"
  top_notes="Cycle ${cycle} top-branch clone of tagged sparse probing."

  alt_id="hash.robinhood.tagged.sparse.cycle${cycle}.v1"
  alt_parent="hash.robinhood.tagged.sparse.v1"
  alt_notes="Cycle ${cycle} non-top branch clone of robin-hood tagged sparse probing."

  if ! rg -q "Id: \"$top_id\"" "$STRING_CATALOG"; then
    insert_node_block "$STRING_CATALOG" "$top_id" "$top_parent" "$top_notes" "TaggedLinearProbingSparseStringDictionary"
    insert_node_block "$UTF8_CATALOG" "$top_id" "$top_parent" "$top_notes" "TaggedLinearProbingSparseUtf8Dictionary"
  fi

  if ! rg -q "Id: \"$alt_id\"" "$STRING_CATALOG"; then
    insert_node_block "$STRING_CATALOG" "$alt_id" "$alt_parent" "$alt_notes" "RobinHoodTaggedSparseStringDictionary"
    insert_node_block "$UTF8_CATALOG" "$alt_id" "$alt_parent" "$alt_notes" "RobinHoodTaggedSparseUtf8Dictionary"
  fi

  dotnet build -c Release Plank.sln
  dotnet test --project Plank.DictionaryLab.Tests/Plank.DictionaryLab.Tests.csproj -c Release

  state_file="$ARTIFACT_DIR/dictionary-lab-explorer.cycle-${cycle}.json"
  rm -f "$state_file"
  dotnet run -c Release --project Plank.DictionaryLab.Benchmarks -- --explore --state "$state_file" --rounds 60 --rows 120000 --explore-weight 0.75

  refresh_report "$state_file"
  latest_state="$state_file"

done

echo "OPTIMIZATION_DONE cycles=$cycle latest_state=$latest_state"
