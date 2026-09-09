# Discovery metadata limits per operating system

Serial Scout's discovery layer exposes the same `NormalizedPort` model everywhere, but the
metadata behind it comes from very different OS mechanisms. This document records what each
platform can and cannot provide, so profile matching (issue #3) treats missing fields as
*expected gaps* rather than anomalies, and so users are never promised identity data the OS
does not emit.

Core principle: **the normalizer never invents values.** Any field the OS does not expose
stays `null`, and identity matching must cope with sparse data.

## Windows

Windows discovery reads the PnP device store (SetupAPI, Ports device class) — the same
data Device Manager shows.

| Field | Source | Can be missing because |
| --- | --- | --- |
| Port path (`COMx`) | PnP friendly name / device description | Rarely; rows without a `(COMx)` suffix are skipped entirely (e.g. Bluetooth SPP enumerators). |
| VID / PID | `USB\VID_xxxx&PID_yyyy` hardware ID | Non-USB ports (internal UARTs, Bluetooth SPP, virtual cables like com0com) have no USB hardware ID. |
| Manufacturer | — | Not read in the first slice; Windows manufacturer strings need per-device `SetupDiGetDeviceRegistryProperty` + localized MUI resolution, which adds failure modes for little gain over `Product`. |
| Product | Friendly name with the `(COMx)` suffix stripped | Friendly name can be absent; falls back to the generic device description. |
| Serial number | PnP instance path, third segment | Most consumer USB-serial chips ship without a real iSerialNumber; Windows then substitutes a **bus-topology location ID** (contains `&`). Those are rejected — they change with the physical port, so presenting them as a serial would create unstable "identities". |

Health probing:

- Availability comes from a **zero-access** `CreateFile` (`desiredAccess = 0`), which
  queries the port without reading, writing, or changing settings.
- Win32 error `5` (access denied) and `32` (sharing violation) both mean "another process
  holds the port" for most USB-serial drivers → `busy`.
- Caveat: some drivers (notably certain Bluetooth SPP bindings) react to *any* open
  attempt, even query opens. `WindowsSetupApiSource(probeAvailability: false)` opts out
  and reports `unknown` states instead of risking side effects.

## macOS

macOS discovery parses an `ioreg` dump of the IOKit registry and lifts USB identity keys
(`idVendor`, `idProduct`, `USB Vendor Name`, `USB Product Name`, `iSerialNumber`) from the
nearest USB-ish ancestor of each `IOSerialBSDClient`.

| Field | Source | Can be missing because |
| --- | --- | --- |
| Port path (`/dev/cu.*`) | `IOCalloutDevice` property | Never for BSD serial clients; the callout node is the addressable device. `/dev/tty.*` exists too but opens block on DCD, so Serial Scout always targets `cu`. |
| VID / PID | `idVendor` / `idProduct` on the `IOUSBHostDevice`/`IOUSBDevice` ancestor | Built-in SoC UARTs and debug consoles (`/dev/cu.debug-console`) have no USB ancestry at all. |
| Manufacturer | `USB Vendor Name` string descriptor | Optional USB descriptor; many cheap adapters omit it or reuse a generic string. |
| Product | `USB Product Name` string descriptor | Same — optional string descriptor. |
| Serial number | `iSerialNumber` string descriptor | Optional; chips like CH340 often ship without one. There is no topology-substitute hazard here because macOS simply omits the key. |

Health probing:

- Availability comes from opening the callout device with `O_EXLOCK | O_NONBLOCK`:
  it takes the same advisory lock `cu`/`screen` hold, then closes immediately. No reads,
  writes, or `ioctl` calls, so DTR/RTS are never toggled and boards are not reset.
- `EWOULDBLOCK` (35) → `busy`. `EACCES`/`EPERM` → `permission-denied`.
- macOS has no per-user gate for listing, but **opening** a `cu` node can fail with
  `EACCES` when the device group excludes the user; Serial Scout reports
  `permission-denied` rather than silently dropping the port.
- A port that disappears between enumeration and probe is dropped from the scan (it is
  simply gone).

## Shared normalization rules

- USB IDs accept `0x`-prefixed hex, plain hex, and `VID_`/`PID_` forms; anything else
  becomes `null` and is annotated in `Notes` (`vendor-id-unparsed`,
  `product-id-unparsed`) so UI can explain the gap.
- Scan states are explicit everywhere: `ready`, `busy`, `permission-denied`, `unknown`.
  `unknown` is a first-class answer meaning "cannot be proven", not an error.
- Results are ordered with numeric-aware port-path comparison (`COM2` < `COM10`), giving
  UI lists and snapshot diffs a stable baseline.
