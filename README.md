# SplitThrottle — Nuclear Option

Independent engine throttle control for HOTAS dual throttle setups.

Each throttle lever controls its own engine side (left/right). Works with twin-engine jets, quad-engine aircraft, and twin-prop planes. Afterburner effects respond per-engine — one side at full AB, the other at idle.

## Features

- **Automatic activation** — activates on supported aircraft, disables on helicopters/VTOL
- **HOTAS dual throttle** — reads joystick axes directly via Rewired controller API
- **Keyboard fallback** — bind individual engine up/down via in-game Controls menu
- **Per-engine afterburner** — visual flame effect matches individual throttle position
- **Configurable** — axis indices, controller name filter, aircraft blacklist, invert options

## Supported Engine Types

| Type | Aircraft |
|------|----------|
| Turbojet | Ifrit, Revoker, Darkreach, etc. |
| DuctedFan | Vortex, Medusa (tiltrotor) |
| ConstantSpeedProp | Cricket |
| PropFan | Brawler |

## Disabled Aircraft (configurable)

Chicane (attack helo), Tarantula, Ibis — these use collective/shared throttle.

## Installation

1. Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx)
2. Install [Extra Input Framework](https://github.com/Assassin1076/NuclearOptionInputFramework/releases)
3. Drop `SplitThrottle.dll` into `BepInEx/plugins/`
4. Launch game, go to **Settings > Controls** to bind keys
5. First launch requires game restart for Rewired action registration

## HOTAS Setup

Edit `BepInEx/config/com.noms.splitthrottle.cfg`:

```ini
[HOTAS]
LeftAxisIndex = 1       # Joystick axis for left throttle lever
RightAxisIndex = 0      # Joystick axis for right throttle lever
ControllerName = THROTTLE  # Partial name match for your throttle device
InvertLeft = false
InvertRight = false
```

## In-Game Keybinds (Settings > Controls)

| Action | Description |
|--------|-------------|
| SplitThrottle::LeftUp | Increase left engine |
| SplitThrottle::LeftDown | Decrease left engine |
| SplitThrottle::RightUp | Increase right engine |
| SplitThrottle::RightDown | Decrease right engine |
| SplitThrottle::Sync | Sync both to current throttle |

## Requirements

- [BepInEx 5.x](https://github.com/BepInEx/BepInEx)
- [Extra Input Framework](https://github.com/Assassin1076/NuclearOptionInputFramework)
