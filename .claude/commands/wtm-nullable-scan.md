Scan nullable modernization progress in WTM:

1. Count files with `#nullable disable` in `src/` — report total
2. Group them by directory (e.g., Core/Models: 12, Core/Grid: 8)
3. Compare against the known baseline (156 files as of 8.1.14)
4. Suggest the next 3-5 files to modernize based on dependency order:
   - Prioritize files that are imported by many others
   - Avoid files with heavy legacy patterns that would be high-risk
5. Output as a markdown table
