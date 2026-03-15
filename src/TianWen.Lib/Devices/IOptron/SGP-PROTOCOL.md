# iOptron SkyGuider Pro (SGP) Serial Protocol

## Serial Configuration
- **Baud rate**: 28800
- **Encoding**: ASCII
- **Terminator**: `#` (hash character)
- **Command prefix**: `:M` (mount command)
- **Response prefix**: `:H` (host response) or `:R` (read response)

## Command Reference

### Mount Info

| Command | Response | Description |
|---------|----------|-------------|
| `:MRSVE#` | `:RMRVE12xxxxxx#` | Firmware version. `12` identifies SGP, `xxxxxx` is date (YYMMDD) |

### Mount Status

| Command | Response | Description |
|---------|----------|-------------|
| `:MGAS#` | `:HRAS01{TR}{SP}0{HEM}1{xx}#` | Get axis status (see breakdown below) |

`:HRAS013501105#` field breakdown:
```
:HRAS 01 3 5 0 1 1 05
      ^^ ^ ^ ^ ^ ^ ^^
      |  | | | | | +-- delay trigger? (unknown)
      |  | | | | +---- fixed (always 1?)
      |  | | | +------ hemisphere (1=north, 0=south)
      |  | | +-------- fixed separator (always 0?)
      |  | +---------- speed index (0-7)
      |  +------------ tracking rate (0=solar, 1=lunar, 2=half-sidereal, 3=sidereal)
      +--------------- fixed prefix (always 01)
```

### Hemisphere

| Command | Response | Description |
|---------|----------|-------------|
| `:MSHE0#` | `:HRHE0#` | Set southern hemisphere |
| `:MSHE1#` | `:HRHE0#` | Set northern hemisphere |

### Tracking Rate

| Command | Response | Description |
|---------|----------|-------------|
| `:MSTR0#` | `:HRTR0#` | Solar tracking |
| `:MSTR1#` | `:HRTR0#` | Lunar tracking |
| `:MSTR2#` | `:HRTR0#` | Half-sidereal tracking |
| `:MSTR3#` | `:HRTR0#` | Sidereal tracking |

### RA Movement

| Command | Response | Description |
|---------|----------|-------------|
| `:MSMR0#` | `:HRMR0#` | Move east |
| `:MSMR1#` | `:HRMR1#` | Move west |
| `:MSMR2#` | `:HRMR2#` | Stop (resume tracking) |

### Slew Speed

| Command | Response | Description |
|---------|----------|-------------|
| `:MSMS1#` | `:HRMS0#` | 1x sidereal (guide speed) |
| `:MSMS2#` | `:HRMS0#` | 2x sidereal |
| `:MSMS3#` | `:HRMS0#` | 8x sidereal |
| `:MSMS4#` | `:HRMS0#` | 16x sidereal |
| `:MSMS5#` | `:HRMS0#` | 64x sidereal |
| `:MSMS6#` | `:HRMS0#` | 128x sidereal |
| `:MSMS7#` | `:HRMS0#` | 144x sidereal (max) |

Speed multiples of sidereal rate (15.0417 arcsec/sec): 1, 2, 8, 16, 64, 128, 144.

### Guide Rate

| Command | Response | Description |
|---------|----------|-------------|
| `:MGGR` | `:HRGR{xx}{yy}#` | Get guide rate (RA=xx, DEC=yy, 0-99). **Note: unterminated command** |
| `:MSGR{nn}{mm}#` | `:HRGR0#` | Set guide rate (nn=RA 0-99, mm=DEC 0-99) |

Default: `:MSGR5050#` = 50% RA, 50% DEC.

### Camera Snap

| Command | Response | Description |
|---------|----------|-------------|
| `:MGCS#` | `:HRCSxyaaaabbbcccdddppkkkkk#` | Get camera settings |
| `:MSCA{y}{aaaa}{bbb}{ccc}#` | `:HRCA0#` | Trigger camera snap |

#### MGCS Response Breakdown

`:HRCS0100300050020000000000#`:
```
:HRCS xy aaaa bbb ccc ddd pp kkkkk
      01 0030 005 002 000 00 00000
      ^^ ^^^^ ^^^ ^^^ ^^^ ^^ ^^^^^
      |  |    |   |   |   |  +-- unknown
      |  |    |   |   |   +----- unknown
      |  |    |   |   +--------- unknown (not sent in MSCA)
      |  |    |   +------------- shot count
      |  |    +----------------- interval
      |  +---------------------- shutter length
      +------------------------- x=unknown, y=active/start flag
```

**Units for shutter and interval are TBD** (possibly seconds).

#### MSCA Command

Only sends `{y}{aaaa}{bbb}{ccc}` (11 chars after `:MSCA`):
- `y` = start flag (1=start)
- `aaaa` = shutter length (4 digits)
- `bbb` = interval (3 digits)
- `ccc` = shot count (3 digits)

Example: `:MSCA10030005002#` = start, shutter=30, interval=5, 2 shots.

### Eyepiece Light

| Command | Response | Description |
|---------|----------|-------------|
| `:MGEL` | `:HREL{x}#` | Get intensity (0-9). **Note: unterminated command** |
| `:MSEL{x}#` | `:HREL{x}#` | Set intensity (x=0-9) |

## Firmware Analysis (SGPro_20170518.bin)

- **MCU**: STM32F103 (medium density, Cortex-M3)
- **Size**: 22,029 bytes
- **SRAM usage**: ~5KB (SP = `0x20001468`)
- **Firmware version string**: `01.01.00` at offset `0x54F7`
- **Firmware date**: `170518` at offset `0x54F0`

### Flash Configuration
- Config area: page 37 (`0x08009400`-`0x080097FF`)
- Stores numeric motor/tracking parameters only (no strings)
- Flash unlock keys at offset `0x1400`: `0x45670123` / `0xCDEF89AB`

### UARTs
- 9600 baud (offset `0x5570`) — HC-to-mount-head channel
- 28800 baud (offset `0x556C`) — main control protocol

### Device Identity
- No serial number: firmware does **not** read STM32 hardware UID (`0x1FFFF7E8`)
- No user string storage in flash config area
- STM32 Option Bytes (`0x1FFFF800`) referenced only for flash protection

### Response Templates in Firmware

| Offset | Template | Notes |
|--------|----------|-------|
| `0x1DE4` | `:HRCSxyaaaabbbcccdddppkkkkk#` | Camera settings |
| `0x1E54` | `:RMRVE12yyyyyy#` | Firmware version |
| `0x1E68` | `:HRVE121212xxxxxxyyyyyyzzzzzz#` | Extended version (query cmd unknown) |
| `0x22D0` | `:HRASx0xxxxxxx#` | Axis status |
| `0x230C` | `:HRGRxxxx#` | Guide rate |

Format strings `%04d`, `%03d`, `%02d`, `%05d` at offsets `0x1E10`-`0x1E3C` fill template placeholders.

### Miscellaneous
- `LES`/`RGG` at `0x1DC0`/`0x1DC4`: 3-char strings followed by coefficient tables (PEC curves? motor params?)
- Custom 16-byte header before vector table: bytes include `0x11=17`, `0x05=5`, `0x12=18` encoding firmware date
