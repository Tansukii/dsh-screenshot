# Changelog

## 1.0.4

- Dark mode: the magnifier card no longer draws an outline. The hard white border showed as a
  bright line beside the text block, and a grey replacement still read as a strip, so the card
  is drawn bare in dark mode; light mode keeps its white outline.
- The magnifier hint reads `按Ctrl+C复制色号` / `Press Ctrl+C to copy the colour`.

## 1.0.3

- The magnifier hint now reads `按Ctrl+C复制色号` / `Press Ctrl+C to copy the colour`.

## 1.0.2

- Fix: every capture now runs on its own STA thread with a fresh message loop. Reusing the
  thread left a stale WinForms context, so the second and later captures died in
  `Control.CreateHandle()` with `ObjectDisposedException: cannot access a disposed object: "SnipForm"`.

## 1.0.1

- Fix: the session message loop could wedge after a capture closed, so later presses were
  acknowledged but never showed the overlay (the "click does nothing" bug).
- Fix: guard every window message handler against a disposed form (the
  "cannot access a disposed object: SnipForm" dialog).
- Fix: the toolbar flashed at the bottom of the screen before moving to its real position.
- Fix: the magnifier stopped refreshing while the pointer slid along a screen edge.
- Fix: right click cancels the capture without leaking the desktop context menu.
- New: the UI follows the DSH language (zh / en) on every capture.
- Docs: bilingual README, real screenshots, MIT licence.

## 1.0.0

- First release: instant region capture, annotations, colour picker, magnifier.
