- **Filter's whole-word option gave different results on non-English text depending on timing** — with the
  whole-word toggle on, a term surrounded by non-Latin characters (e.g. CJK) matched or didn't depending on
  whether the line index had finished building, because the two code paths that implement the filter disagreed
  about what counts as a word character. Both now use the same rule (issue #164).
