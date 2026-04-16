Show git status, current branch, and last commit across all SharpAstro sibling repos.

Check each of these repos:
- `../../sharpastro/Fonts.Lib` (SharpAstro.Fonts)
- `../../sharpastro/DIR.Lib` (DIR.Lib)
- `../../sharpastro/Console.Lib` (Console.Lib)
- `../../sharpastro/SdlVulkan.Renderer` (SdlVulkan.Renderer)
- `.` (TianWen - current repo)

For each repo, show:
1. Current branch name
2. Commits ahead/behind remote (if tracking)
3. Any uncommitted changes (short status)
4. Last commit message (one line)
5. The VersionPrefix from the main .csproj

Format as a compact table. Flag any repo that has uncommitted changes or is ahead of remote.
