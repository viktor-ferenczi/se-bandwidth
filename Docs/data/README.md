# Documentation working data

This folder holds machine-generated working data for the documentation pipeline, kept
separate from the human-readable documentation tree in `Docs/`.

- `manifest.jsonl` — one JSON object per tracked source file: `path`, `size`, and a
  `sha256` content hash. The hash is the cache key: on a re-run, a file whose hash is
  unchanged needs no re-reading, so only the changed files (and the pages that depend on
  them) have to be revisited.

Nothing here is required to read the documentation. The whole `Docs/data/` folder can be
deleted at any time, or excluded from version control, without affecting the docs.

To regenerate the manifest from the repository root:

```bash
git ls-files | grep -vE '\.(dll|pdb|cache|png)$' | grep -vE '/(bin|obj)/' | grep -vE '^\.idea/' | \
while read -r f; do
  [ -f "$f" ] && printf '{"path":"%s","size":%s,"sha256":"%s"}\n' \
    "$f" "$(wc -c < "$f" | tr -d ' ')" "$(sha256sum "$f" | cut -d' ' -f1)"
done > Docs/data/manifest.jsonl
```
