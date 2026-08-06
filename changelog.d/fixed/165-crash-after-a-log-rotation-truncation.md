- **Crash after a log rotation / truncation** — if the open file shrank on disk (`logrotate` copytruncate,
  `> app.log`) while the app still held the longer index, the next repaint could throw an unhandled
  `IndexOutOfRangeException` and terminate the app. The line lookup now clamps to the last line that still
  exists, matching how offset-to-line already behaved (issue #165).
