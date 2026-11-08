# Dice and turn

Dice and the turn counter are on the right of the main toolbar and in the **Tools** menu. They need an open game. Every roll and every turn change is written to the [log](Window-Log-and-Panels#log), and the last roll also shows in the status bar (for example "Roll: 2d6 = 3 + 4 = 7") unless you switch that off in [Settings](Window-Settings#dice-and-log).

## Dice

| Control | What it does |
|---|---|
| **1d6** button, **Tools > Dice > 1d6** | Rolls one six-sided die. Shortcut F5 |
| **2d6** button, **Tools > Dice > 2d6** | Rolls two six-sided dice. Shortcut F6 |
| **Dice ▾** button, **Tools > Dice** | The same two rolls, then any dice the module defines (named as in the module), then **Custom...** and **Multi-roll...** |

**Custom...** opens the **Custom Roll** dialog: type a dice expression in **Dice (e.g. 2d6 or 3d10+2)**. A mistake is shown in red under the box and **OK** stays greyed out until it is fixed.

**Multi-roll...** opens the same dialog with a **Times:** box (1 to 20) and rolls the expression that many times, logged as "Multi-roll 1/3", "Multi-roll 2/3" and so on.

### Physical dice

If you would rather roll real dice, tick **Physical dice: ask me for each die instead of rolling** in [Settings](Window-Settings#dice-and-log). Every roll then asks "Value of die 1 of 2 (1–6):" and so on, and logs what you type. The [formula dialog](Window-Formula-Dialog) has its own **Physical dice** box for its rolls.

## Turn

| Control | What it does |
|---|---|
| **Next turn** button, **Tools > Turn > Next turn** | Adds one to the turn counter |
| **Tools > Turn > Previous turn** | Takes one off (not below turn 1) |
| **Tools > Turn > Set...** | The **Set Turn** dialog: a **Turn:** number and an optional **Phase:** text |

The status bar shows the turn and phase, for example "Turn 2 · North Supply".

With a rules pack that has phases, use **Next Phase** in the [phase panel](Window-Phases#phase-panel) (or Ctrl+Right) instead: it moves through the phases and starts the next turn after the last one. Ctrl+Right only steps the plain turn counter when the rules pack has no phases.

While a turn from another player is being replayed, and in a read-only viewer copy, the dice and turn controls are greyed out.
