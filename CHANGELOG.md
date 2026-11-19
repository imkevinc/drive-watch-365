# Changelog

## 1.0.0

First public release.

- Lists every drive Windows can see, with a 30-day strip of day squares per drive.
- Checks at every login, once a day, and on demand. After a reboot it looks again a few times
  before raising the alarm, because drives can be slow to appear.
- Tells you when a drive goes missing; **I removed it** tells the app you pulled one on purpose.
- Permanent **problem history**: one record per disappearance (not one per day), with how long the
  drive was gone, repeat-trouble tags, slow-to-appear drives, and health changes.
- **Drive health** (SMART) with a one-time admin startup task: temperature, power-on time, bad-sector
  counts, SSD life, the full attribute table, and daily trends. Judged by how numbers move, not
  just whether they are zero.
- Nicknames and notes for each drive.
- System tray icon (green / amber / red), single instance, optional start with Windows.
- `--demo` mode with made-up drives, used for the screenshots.
