# Final native design cross-review — 2026-10-03

Evidence reviewed together: all 17 full reference/native pairs in
`build/linux/design-final`, plus focused crops for 08, 10 and 11 and the Shell
panel/header comparisons. GTK captures are `build/linux/ui-final`; Shell captures
are `build/linux/shell-scenes-pointer-final`. Review uses the approved common token
and typography contract. A missing uniform 2px measurement is not itself a defect.
After the final 08/11/12/16 recaptures, no unresolved P0/P1/P2 visual defect was
found. The findings below distinguish repairs from minor remaining differences.

## Findings

- **Resolved P2, 08 HDR monitor row alignment:** the monitor name/connector block sat
  approximately 20px above the row's visual center while its icon, resolution and
  switch are centered. In the focused crop, the reference first row begins near
  y=200 and its title near y=230; native begins near y=205 and title near y=214.
  This repeats on all four rows. Center the two-line info box vertically and
  recapture. The final recapture verifies centered text matching the other row
  controls; this was the only additional P2 found during this cross-review.
- **Resolved P3, 11 current-step marker:** the loading step previously used the
  same outlined gray numbered circle as pending steps. The final recapture has an
  olive active ring. Its motion differs from the reference spinner, but current
  state is clear from the ring, adjacent progress bar and current-step text.
- **Resolved P3, 05 comparison state:** the captured filter popup has draft selections but
  no applied-filter count beside its trigger. The source now implements the
  count for applied filters. The separate real-pointer capture
  `build/linux/ui-filter-interaction/filter-applied.png` demonstrates the 3-group
  badge and 5 matching results. The draft-only reference pair is a fixture-state
  discrepancy rather than a missing control.
- **Resolved P3, 12 timestamp presentation:** the final recapture uses explicit
  HH:mm:ss instead of the previous localized `10시 24분 0초`. Fixture seconds
  differ, which is expected test content.
- **P3, 14 quick menu density:** some groups are 5–10px shorter than the reference.
  Every control fits; scrolling at 1000×700 with enlarged text was separately
  verified. Production correctly omits the reference's design-preview footer.
- **Resolved P3, 16 empty-state spacing:** increasing group spacing brings the
  icon-to-final-button block close to the reference's overall vertical rhythm.
  It remains centered, readable and usable. The native frame still lacks the reference's lower-left collection-state
  caption, though recording state remains available through the pause control.

## Screen review

| State | Result in reviewed captures |
| --- | --- |
| 01 library | No P0/P1/P2; three columns, selection and separate action bar present |
| 02 shortcut | No P0/P1/P2; exact specified 1440×488 popup and 288×236 cards |
| 03 AI search | No P0/P1/P2; scoped result summary and card actions fit |
| 04 detail | Image width correction verified; no text/action clipping |
| 05 filter | Applied count verified separately with real pointer interaction |
| 06 clipboard settings | No P0/P1/P2; fixed Antigravity provider plus enable toggle is a deliberate functional adaptation |
| 07 sound | No P0/P1/P2; numeric controls and actual filter response replace generated approximations |
| 08 HDR | P2 vertical alignment repaired and independently recaptured |
| 09 inhibitors | No P0/P1/P2; screen and sleep requests remain separate |
| 10 Antigravity | Previous prompt-height/response-clipping P2 resolved; entire response and footer visible |
| 11 vLLM | No P0/P1/P2; active-step olive ring correction verified |
| 12 diagnostics | Previous missing event filter resolved; severity icons now follow actual level |
| 13 settings | Previous extension-control difference resolved; actual toggle and status visible |
| 14 quick menu | No P0/P1/P2; density P3 above |
| 15 collections | No P0/P1/P2; Mutter-centered modal and complete controls |
| 16 empty | No P0/P1/P2; spacing correction verified; collection-state caption P3 remains |
| 17 AI error | No P0/P1/P2; retry, ordinary search and connection settings all visible |

Expected differences include actual Yaru symbolic glyphs, pinned native font
rasterization versus generated lettering, generated thumbnail composition,
real device/model state, left/right quick-panel navigation, and main-window Enter
copy. Surrounding example wallpaper/editor content is outside the owned Shell UI.
These captures alone do not validate actual device or service behavior; functional
evidence is recorded separately.
