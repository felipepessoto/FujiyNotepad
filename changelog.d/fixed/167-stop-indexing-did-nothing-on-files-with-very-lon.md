- **"Stop indexing" did nothing on files with very long lines** — cancellation was only checked once per line
  found, so on a file with a huge single line (minified JS/JSON, a base64 blob, a one-line SQL dump) stopping
  the index — and therefore opening another file, reloading, or closing — waited for the scan to reach the next
  newline, making the app look hung. The scan is now cancelled directly (issue #167).
