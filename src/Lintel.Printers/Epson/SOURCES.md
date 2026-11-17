# Sources for the Epson code

Only protocol facts (byte layouts, per-model addresses, keys, divisors) were taken from these projects. The C# is original.

| What | Source | Licence |
|---|---|---|
| IEEE 1284.4 handshake, EPSON-CTRL framing, usbprint device path, EEPROM read/write frame layout | epson-usb 0.1.2 (PyPI, github.com/onur-kesim/epson-l3251-usb-reset): `d4.py`, `epson_ctrl.py`, `backends/usbprint_win.py` | MIT |
| EEPROM opcode derivation (41 BE A0 / 42 BD 21), `||` command, caesar-shifted write key, waste counter byte order and divisors, L3150 entry, "temporary reset" notes | Ircama/epson_print_conf `epson_print_conf.py` (`eeprom_oid_read_address`, `eeprom_oid_write_address`, `get_waste_ink_levels`, `PRINTER_CONFIG` ET-2812 / L3150) | EUPL-1.2 (compatible with GPL-3.0 under EUPL Appendix) |
| L3250 data block: read key `4A 36`, waste counters (main 0x30/0x31/0x2F max 6346, borderless 0x32/0x33/0x2F max 3416, third 0xFC-0xFE max 1300), reset byte list, commit step (0x100 AND 0xFE), model list sharing this block (L3250-L3259, ET-2400 series, ET-2810 series) | CiRIP/ez-reset `src/ez_reset/devices.xml`, element `<L3250>` and the `<printer ... specs="EPSON,L3250,...">` rows | AGPL-3.0 (numeric device facts only; AGPL-3.0 and GPL-3.0 may be combined) |
| Remote-mode commands: `ESC ( R 08 00 00 "REMOTE1"`, `ESC 00 00 00`, `NC 02 00 00 00`, `CH 02 00 00 xx` | Gutenprint reference manual, "Remote Mode Commands" (gimp-print.sourceforge.io/reference-html/x952.html); `commandtoescpx.c` in Gutenprint/CUPS drivers | GPL-2.0-or-later |
| Maintenance job layout (leave packet mode `@EJL 1284.4`, `ESC @` x2, `TI` clock sync, `CH`, `JE` job end), power clean = CH group byte OR 0x10 | Ircama/epson_escp2 1.0.4 `epson_encode.py` (`clean_nozzles`, `check_nozzles`), which cites abrasive/x900-otsakupuhastajat `emanage.py` for the 0x10 flag | EUPL-1.2 |

## Limits and guidance in `EpsonModelLimits` for the L3250

* 100 % of the main pad = raw 6346, borderless = raw 3416 (the `max` values in ez-reset's L3250 block; epson_print_conf's dividers 63.46 and 34.16 are the same numbers).
* Epson user guides (L3250 / L3210 series, download4.epson.biz `sec_pubs/l3250_series`, `l3210_series`): if three head cleanings do not help, switch the printer off and wait at least 12 hours; Power Cleaning uses a lot of ink and shortens pad life; each ink tank must be at least one third full; wait at least 12 hours between power cleanings.
* Epson Europe FAQ KA-02055 "When to perform a Power Clean": try several standard head cleans first; wait at least 12 hours after the last head clean; avoid repeated power cleans; may refuse to run when the maintenance box is nearly full.
* NOT found in any source: ink volume or pad-counter increment per head cleaning or power cleaning, rated monthly duty cycle for the L3250. They are deliberately not recorded.

## Verified on hardware

Read path (D4 over the usbprint interface, `||` EEPROM reads) was run against an EPSON L3250 Series over USB. Maintenance commands (RAW job) and the waste-counter reset write path were implemented and unit-tested against recorded byte layouts only; they have not been run on a device.
