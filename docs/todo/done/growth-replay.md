# Replaying what grew between two scans — design

> **Status:** ✅ COMPLETE — the replay landed under
> [#285](https://github.com/BootBlock/Deguffer/issues/285). This records the two questions the issue
> left open and how each was settled.

The map coloured by growth (#260) shows one moment. The replay lets the reader watch the change: a
scrubber and a play button in the **What grew** card move the map from the earlier scan to now, with
the layout animation from #282 carrying each shape from one step to the next.

## How a partial record is shown honestly

A `ScanSummary` records the largest folders of the earlier scan and every folder above them, and
nothing else. The earlier picture is rebuilt from that alone (`GrowthReplay`):

- **A recorded folder** starts at the size the earlier scan recorded.
- **What a recorded folder held and the record does not name** (its files, and its folders no larger
  than `UnrecordedAtMost`) is **one block per folder**, labelled "Files and folders under *n*, not
  recorded", or "Files, not recorded" where every folder was recorded. It is painted the growth
  palette's "Not compared" grey, because nothing compares it.
- **A folder recorded then and gone now**, from the disk or from the page since the scan, starts at
  its earlier size, is labelled "removed since", and shrinks away.
- **Whatever is on the disk now and was not recorded** grows from nothing. A block and the files that
  replaced it trade places rather than one turning into the other: nothing says which files were
  there then.
- **Between the two ends nothing was measured.** Each size moves in a straight line, and the card
  says the steps are estimates, not scans. The approximations `ScanGrowth` already states (a change
  of route, a lower bound) are stated as before.
- **The volume beside the root** moves from the earlier capacity and free space to now's. What
  Windows states about the space no folder accounts for (shadow copies, reserved storage) was read
  with this scan only, so it is not drawn before now. That space is in the unaccounted block until
  then.
- **A folder the earlier scan did not record** has no earlier picture. The card says so, and the
  scrubber and play button are disabled there.

Every shape keeps the colour of the change it ends with at every step, the same colour the map
coloured by growth gives it now, so colour never moves while shape does.

## Whether the movement reads as growth rather than reshuffling

Each step is laid out as any treemap is, so children are ordered by their size at that step. A fixed
order across steps was considered and rejected: the squarified layout closes a row at the first child
with no area, so a fixed order that put an empty child ahead of a full one would push every later
child into the aggregate.

What keeps the movement readable:

- **Twelve steps**, each a quarter-second move (`MotionToken.Shift`), so a reorder happens as one
  folder passes another, not all at once.
- **Siblings of one size keep their name order** at every step (#309), so only a real change of size
  moves a shape.
- **Node numbers are stable.** The nodes of the tree now keep their numbers at every step, and what
  only the earlier scan had is numbered after them, so `ShapeChanges` matches every shape between two
  steps and into the tree now. Leaving the replay at "Now" is the same morph as any other step.
- **Colour is constant**, as above, so the eye follows a shape by its colour while it moves.

## Constraints kept

- **A picture only (§7.1).** `MapReading.Answers` is false for a step, so a drawing of one answers no
  hover, pick, menu or double-click, and outlines nothing the page has picked. A click leaves the
  selection as it was. While a step is on screen the selection's commands are disabled.
- **The summary stays on this machine.** The replay reads the summary already in memory for the
  comparison, and writes nothing.
- **Reduced motion (#273).** With animation effects off the scrubber has only its two ends, and a
  play shows the earlier scan for a moment and then now, each change a crossfade.
- **The replay goes back to "Now"** when the comparison, the tree, the folder on screen or the
  colouring changes, so a step is never of anything but the picture the reader asked to replay.

## Where it lives

| Piece | Type |
| --- | --- |
| The tree at each step, colours and labels | `GrowthReplay`, `GrowthReplayFrame`, `ShapeColours.ForReplay`, `GrowthText.Unrecorded` (Core) |
| The refusal of the pointer | `MapReading.Answers` (Core), `ExploreMap.Pick` and `OnRightTapped` |
| Where the scrubber is, and a play | `ExploreReplay` (view model), `ExploreReplayClock` (the page's clock) |
| The controls | The **What grew** card in `ExplorePage.xaml` |
