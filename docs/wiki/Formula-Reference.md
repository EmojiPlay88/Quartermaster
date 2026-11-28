# Formula Reference

Formulas are the small sums you write in the Rules Workbench: how much fuel a unit draws, whether a unit shows up in a filter, when an alert fires. This page lists everything the formula language can do. To see formulas in use, follow [Making a Rules Pack](Making-a-Rules-Pack). To run one, see the [Formula dialog](Window-Formula-Dialog).

The examples use the invented Riverland demo game: units have the properties Fuel, Ammo and Readiness, depots keep Stores, and the global holder keeps Weather. After most examples the page says what the formula gives.

## The basics

- **Three kinds of value.** A **number** (`3`, `0.5`), **text** in double quotes (`"Unit"`), and **true/false**. Numbers show with at most two decimals.
- **Literals.** Write numbers with digits and an optional decimal point (`2`, `.5`, `1.25`). There is no minus sign inside a number: `-3` is the minus operator applied to 3. Write text between double quotes. To put a quote inside text, write `\"`, and for a backslash write `\\`. Nothing else may follow a backslash. `true` and `false` are the two true/false values.
- **Names.** Anything else you type is a variable name: letters, digits and underscores, starting with a letter or underscore (`Fuel`, `Hexes`, `Supply_Left`). A name cannot contain a space. Where the name gets its value depends on where the formula is used; see [Where names get their values](#where-names-get-their-values).
- **Capital letters.** Function names, `and`, `or`, `not`, `true` and `false` may be written in any case: `MIN(1, 2)` and `min(1, 2)` are the same. Variable names (the names of bindings) must match the name you gave them exactly, capitals included. In a filter, a bare name is a property name instead, and property names ignore capitals. Marker keys ignore capitals too (`marker("kind")` finds the marker `Kind`). Comparing text with `==` and `!=` ignores capitals (`"Unit" == "unit"` is true).
- **Spaces and signs.** Spaces are free. For comfort when pasting from a rulebook, the signs `−` (minus), `×` (times) and `÷` (divide) are accepted in place of `-`, `*` and `/`.
- **True/false and numbers.** Where a formula needs a true/false answer (`if`, `and`, `or`, `not`), a number counts as true if it is anything but 0. Text is never accepted there.
- **No percent or power.** There is no `%` (remainder) and no `^` (power). Use `floor`, `round` and multiplication instead.
- **Size.** A formula may be up to 10,000 characters and nest up to 200 levels deep.

```formula
Fuel + 2
```

If Fuel is 3, this gives 5.

## Operators

From the loosest binding to the tightest: `or`, then `and`, then `not`, then comparisons, then `+ -`, then `* /`, then the minus sign in front of a value. Operators at the same level work from left to right. Brackets `( )` change the order.

| Operator | Meaning | Takes | Gives | Level (1 = loosest) |
|---|---|---|---|---|
| `or` | either side is true | true/false or numbers | true/false | 1 |
| `and` | both sides are true | true/false or numbers | true/false | 2 |
| `not` | flips true and false | true/false or a number | true/false | 3 |
| `==` | equal (text ignores capitals) | any two values | true/false | 4 |
| `!=` | not equal | any two values | true/false | 4 |
| `<` | less than | numbers | true/false | 4 |
| `<=` | less than or equal | numbers | true/false | 4 |
| `>` | greater than | numbers | true/false | 4 |
| `>=` | greater than or equal | numbers | true/false | 4 |
| `+` | add | numbers | number | 5 |
| `-` | subtract; also the minus sign in front of one value (`-Fuel`) | numbers | number | 5 (as subtract), 7 (as minus sign) |
| `*` | multiply | numbers | number | 6 |
| `/` | divide | numbers | number | 6 |

Things to know:

- **`and` and `or` stop early.** `has(Fuel) and Fuel < 2` never looks at Fuel when the unit has none, so it is safe. `and` and `or` always give true or false, even when given numbers.
- **`==` and `!=` accept anything**, but values of different kinds are never equal: `1 == "1"` is false. Two numbers are equal only when exactly equal.
- **Comparisons cannot be chained.** `1 < Fuel < 5` is an error. Write `Fuel > 1 and Fuel < 5`.
- **Dividing by 0 is an error**, and so is any result that is not a finite number.

```formula
Fuel < 2 or Ammo == 0
```

True when the unit is low on fuel or out of ammo.

```formula
not (Readiness >= 3) and marker("Kind") == "Unit"
```

True for units whose Readiness is below 3. Because `not` binds looser than a comparison, `not Readiness >= 3` would mean the same thing; the brackets just make it easier to read.

```formula
-Fuel + 10 * 2
```

If Fuel is 3, this gives 17. The minus sign in front of a value binds tightest, so it is applied first (Fuel becomes -3); `10 * 2` is 20; then the two are added: -3 + 20.

## Functions

Each function is written `name(arguments, separated, by commas)`. Names may be written in any case. Giving the wrong number of arguments is an error that says how many the function takes.

### if(test, then, else)

Gives `then` when `test` is true and `else` when it is false. Only the chosen side is worked out. `test` must be true/false or a number (not 0 means true). `then` and `else` may be any kind of value.

```formula
if(Ammo > 0, 1, 0)
```

Gives 1 when Ammo is above 0, otherwise 0.

### min(a, b, ...)

The smallest of two or more numbers.

```formula
min(Fuel + 2, 6)
```

If Fuel is 5, this gives 6: fuel can never go above 6.

### max(a, b, ...)

The largest of two or more numbers.

```formula
max(Fuel - 1, 0)
```

If Fuel is 0, this gives 0 rather than -1.

### round(x) or round(x, digits)

Rounds to the nearest whole number, or to `digits` decimals (a whole number from 0 to 10). Halves round away from zero.

```formula
round(Readiness / 2)
```

If Readiness is 3, this gives 2 (1.5 rounds up). `round(3.14159, 2)` gives 3.14.

### floor(x)

Rounds down to a whole number. `floor(2.9)` gives 2 and `floor(-2.1)` gives -3.

```formula
floor(Readiness / 2)
```

If Readiness is 3, this gives 1.

### ceil(x)

Rounds up to a whole number. `ceil(2.1)` gives 3 and `ceil(-2.9)` gives -2.

```formula
ceil(Moved / 2)
```

One fuel for every two hexes moved, rounded up. If Moved is 3, this gives 2.

### abs(x)

The number without its sign. `abs(-4)` gives 4.

```formula
abs(Fuel - Ammo)
```

If Fuel is 1 and Ammo is 4, this gives 3.

### clamp(x, low, high)

Keeps `x` between `low` and `high`: gives `low` if `x` is below it, `high` if `x` is above it, else `x`. It is an error if `low` is above `high`.

```formula
clamp(Fuel + Gain, 0, 6)
```

If Fuel is 5 and Gain is 3, this gives 6.

### lookup(Table, row) or lookup(Table, row, column)

Reads a cell of a table from the Tables tab. The first argument is the table's **name written bare**, not in quotes (so a table name should be a single word, like `SupplyByDistance`, with no spaces). `row` is the value to find among the table's row keys; give a `column` as well when the table has two dimensions. A one-dimension table takes `row` only. Row and column keys can be single numbers (`3`), ranges (`1-2`), open ranges (`5+` or `4-`) or words. The first key that matches wins. The cell comes back as a number if it looks like one, otherwise as text.

```formula
lookup(SupplyByDistance, Hexes)
```

With the Riverland chart, if Hexes is 2 this gives 2; if Hexes is 7 it gives 0. The table name is not a variable and needs no binding.

### lookuprange(Table, row, value)

For a chart that prints a different range in each cell of a row. The table must have two dimensions. `lookuprange` finds the row the way `lookup` does, then reads each cell of that row as a key (`2-4`, `5+`, `3-`, a single number or a word) and gives the **column key** of the first cell, from left to right, that holds `value`. The column key comes back as a number when it is a number, otherwise as text. It works wherever `lookup` does: formulas, filters, warnings and phase conditions.

```formula
lookuprange(Landing, Altitude, Roll)
```

With a Riverland table `Landing` whose row `2` holds `1-3` under the column `Hard`, `4-5` under `Rough` and `6+` under `Smooth`, a Roll of 5 gives `Rough`. The table name is not a variable and needs no binding.

### count(Roll, low, high)

How many of a Roll binding's dice show a value from `low` to `high`, both ends included. The first argument must be a variable of Kind **Roll**. The roll keeps its value (the total of its dice), so one roll can feed several counts:

```formula
count(Planes, 1, 2) * 10 + count(Planes, 6, 6)
```

With `Planes` rolling 4d6 and the dice 1, 2, 5, 6, this gives 2 * 10 + 1 = 21. The number of dice can come from an expression: see **Dice count** in [Making a Rules Pack](Making-a-Rules-Pack#a-range-chart-and-a-roll-per-plane).

With **Physical dice** on, type the dice of such a roll one by one, separated by spaces or commas (`1 2 5 6`): exactly as many as the roll has, each from 1 to the number of sides. A single typed number is taken as the total; for a roll of more than one die `count` then cannot tell the dice apart (a one-die roll typed as a number is that die). The number of dice rolled is public: a dice count that reads hidden data shows its size in the dice log. `count` works only in a formula run (not in filters, warnings or phase conditions).

### dice(count, sides)

Rolls `count` dice with `sides` sides each, and gives their total. `count` is a whole number from 1 to 100 and `sides` a whole number from 2 to 1000. The roll goes into the shared dice log. Dice work only where a game can roll them (for example in a formula run from the Formula dialog); they are not allowed in filters.

```formula
dice(2, 6)
```

Gives a number from 2 to 12. For a roll the player sees as a named variable, bind a variable of Kind **Roll** instead (see [Where names get their values](#where-names-get-their-values)).

### has(Property)

True when the unit has a value for that property. Use it to keep a formula or filter to the units it makes sense for. Give the property's name bare, or in quotes if you prefer. The name is compared ignoring capitals, so `has("fuel")` finds Fuel.

```formula
has(Fuel) and Fuel < 2
```

True for units that have Fuel and are below 2. `has("Fuel")` means the same as `has(Fuel)`.

### startswith(text, start)

True when `text` begins with `start`. Capitals are ignored. Both arguments must be text.

```formula
startswith(name(), "NL")
```

True for every counter whose name begins with NL, such as "NL 1 Rifles".

### contains(text, part)

True when `part` appears anywhere in `text`. Capitals are ignored. Both arguments must be text.

```formula
contains(name(), "Armour")
```

True for "NL 4 Armour" and "SC 3 Armour".

### name()

The name of the counter the formula is about, as text. Takes 0 arguments: write `name()` with nothing between the brackets. It needs a unit; used with no unit it is an error.

```formula
name() == "NL 6 Recon"
```

True for that one counter.

### marker("Key")

The value of a marker (a custom label built into the counter in the module) as text. The key goes in quotes. If the counter does not have that marker, it is an error. Riverland's counters carry the markers Side, Kind and Type.

```formula
marker("Kind") == "Unit" and marker("Side") == "Northern League"
```

True for the Northern League's fighting units, not its depot or the turn marker.

### layer("name")

The level of a named layer on the counter, as a number: 1 for the first level, 2 for the second and so on, and 0 when the layer is switched off. The name goes in quotes, and capitals and spaces at the ends do not matter. It reads the counter the formula runs on, or the counter a filter is testing (in an Applies to, an alert, a warning, a Ledger filter or a property's starting value). It cannot be used in a phase condition, which reads no counter.

Riverland's forts carry a layer named `Levels` (Fort level 1 to 3, switched off when the fort is not built):

```formula
layer("Levels") >= 2
```

True for a fort at level 2 or 3. The level is what the map shows, so reading it is not hidden information. The exceptions:

- A counter without a layer of that name has no value (the same as a marker it lacks): in a filter it simply does not match, and a formula stops with `{name} has no layer "{layer}"`.
- A layer that follows a property (its level comes from a property's value) cannot be read this way: read the property instead (`Layer "{layer}" follows a property; read the property instead`).
- In a player's view, another side's masked counter shows no layer: `layer() cannot be read in this view`, and the host works it out.
- The name must be quoted text: `layer() needs a layer name in quotes`.

To change a layer from a formula, give the formula a **Set layer** output; see [Outputs](#outputs).

### turn()

The turn number, as a number. Takes 0 arguments. Before the game starts it is 1.

```formula
turn() == 1
```

True only on the first turn.

### stage()

The repeat number inside a repeating block of phases (1 for the first pass, 2 for the second, and so on). Outside a block it is 1. Takes 0 arguments.

```formula
stage() == 2
```

True on the second pass through a block.

### segment()

The repeat number inside an inner block that repeats within another block (1 for the first pass, 2 for the second, and so on). Outside an inner block it is 0. Takes 0 arguments.

```formula
segment() > 3
```

True from the fourth pass through the inner block.

### runs("formula-id") or runs("formula-id", "period")

How many times a formula has been applied. The first argument is the formula's id in quotes (the **Id** shown on the Formulas tab). The optional second argument is the period, in quotes: `"stage"` (the same turn and the same pass of a repeating block; outside a block, the same as the turn), `"turn"` (the same turn) or `"game"` (every turn; the default). On a counter it counts the runs of that formula on this counter; with no counter in play it counts the runs made without a unit. A warning or alert whose condition calls `runs()` of a formula that needs a unit is checked for each counter. Only applied runs count: a run that failed, was refused or was undone is not counted, and the run being checked is not counted yet. A run with no outputs counts once.

```formula
runs("f-bridge-repair", "turn") < 1
```

True when Bridge repair has not yet been applied to this counter this turn: as an **Only runs when** condition it allows one run a turn.

It works in formulas, bindings, preconditions, outputs, entry filters, warnings and alerts, but not in a property's applies-to, a computed property, a starting value, a side's unit filter or a phase condition. The validator reports `runs() needs a formula id in quotes`, `runs() names formula "{id}", which does not exist` and `runs() period must be "stage", "turn" or "game"`. In a player's view of the game, `runs()` on a counter the player does not hold (or with no counter) cannot be counted (`runs() cannot be counted in this view`): a guest's condition that uses it is decided by the host.

### row()

The row key being tested, for an Input's **Choices filter**. It is a number when the key is numeric, otherwise text. It has no arguments and works only in a choices filter, where the counter's own values can be read as well (formula variables cannot). Anywhere else the validator reports `row() can only be used in a choices filter`.

```formula
lookup(Cargo, row(), "Min size") <= Size
```

Offers only the rows of table Cargo whose Min size is within the counter's Size. The filter is tried once for each row key of the Input's **Choices table**; a row for which it is false, or cannot be worked out, is left out of the list. See [Choices from a table](Making-a-Rules-Pack#choices-from-a-table).

### mod(a, b)

The remainder after dividing `a` by `b`, floored: the result has the sign of `b`, so it is never negative when `b` is positive. If `b` is 0 it is an error ("mod: division by zero").

```formula
mod(turn(), 4) == 1
```

True on turns 1, 5, 9 and so on.

### side()

The name of the side the unit belongs to, as text, or `""` (empty text) when the unit has no side. Takes 0 arguments. It needs a unit. It cannot be used inside a side's own unit filter (that filter decides who belongs to the side), so there it is an error, `side() is not available in a side's unit filter`, and the filter is false.

```formula
side() == "North"
```

True for units of the side named North.

### phaseside()

The name of the side whose phase is current, as text, or `""` when the current phase has no side (or the game has not started). Takes 0 arguments. It needs no unit, so it is the one new function a phase condition may use. Compare it with `side()` to ask whether a unit's side is the one that is playing:

```formula
if(side() == phaseside(), 1, 0)
```

Gives 1 for the phasing side's units and 0 for everyone else's.

### map()

The name of the map the unit is on, as text. Takes 0 arguments. It needs a unit.

```formula
map() == "Main Map"
```

True for units on the map named Main Map.

### holder(Holder, Property)

The value of a property on a named holder, such as a depot. Both arguments are bare names, not quoted text and not variables: `holder(Rail, Tonnage)`. It does not read the global holder: `holder(Global, Weather)` is an error. Read a global value by its bare name instead (`Weather`). If the holder has no value for the property yet, the property's Default is used. The property must be a holder property (scope Holders or Both); a piece-only property is an error. In a player's view of the game, another side's holder record is hidden, so `holder()` on it never reads a made-up Default: it is false in a filter and an error in a formula. Your own side's holders are never hidden, so in your view an unset value still reads the Default. A formula that uses `holder()` on another side's holder is masked like a Holder binding of that holder: its working and errors are not shown to the other side.

```formula
holder(Rail, Tonnage) > holder(Rail, Capacity)
```

True when the Rail holder carries more than its Capacity.

### sum(Property, "filter")

The total of a property over every piece the filter matches, anywhere in the game. `Property` is a bare property name, as in `has(...)`; it must be a piece property (scope Pieces or Both). The filter is quoted text written like a unit filter (see [In a filter](#where-names-get-their-values)), tested on each piece in turn; `""` matches every piece. Inside the quotes, write a quote as `\"`. A piece's value is its Ledger value, or the property's Default when it has none, as a Unit binding reads it. That includes pieces the property does not apply to (its Applies to does not match them): they count with the Default too, so filter them out (by kind or type) when they should not count. When no piece matches, the sum is 0. A filter that reads a value a piece does not have, or a marker it does not carry, simply does not match that piece. Works in formulas, filters, warnings, alerts and phase conditions.

```formula
sum(Fuel, "side() == \"Northern League\" and marker(\"Kind\") == \"Unit\"")
```

All the fuel the Northern League's units carry.

### countof("filter")

How many pieces in the game the filter matches, 0 when none do. Takes only the filter.

```formula
countof("marker(\"Type\") == \"Truck\" and side() == \"Southern Compact\"")
```

The number of trucks the Southern Compact has.

### maxof(Property, "filter")

The largest value of the property among the pieces the filter matches. When no piece matches it is an error, `No piece matches "{filter}"`, since there is no largest value; guard it with `countof` when that can happen.

```formula
if(countof("marker(\"Kind\") == \"Unit\"") > 0, maxof(Readiness, "marker(\"Kind\") == \"Unit\""), 0)
```

The best Readiness of any unit, or 0 when there are no units.

### minof(Property, "filter")

The smallest value of the property among the pieces the filter matches. When no piece matches it is an error, `No piece matches "{filter}"`.

```formula
minof(Ammo, "marker(\"Kind\") == \"Unit\"")
```

The lowest ammo of any unit.

### hexsum(Property) or hexsum(Property, "filter")

The total of a property over the pieces in the same hex as the piece being worked out (same map, same grid location), the piece itself included; give a filter to count only some of them. It needs a piece: it works in a formula run on a unit, in a formula's Applies to, and in warnings, alerts and Ledger filters that test units. Elsewhere it is an error, `hexsum() is not available here`; the Workbench refuses it in a side's unit filter and in a property's applies-to. A piece that is not in a hex (off the map grid) is an error, `{piece} is not in a hex`.

```formula
hexsum(Fuel, "marker(\"Kind\") == \"Unit\"")
```

All the fuel of the units stacked in this unit's hex.

### hexcount() or hexcount("filter")

How many pieces are in the same hex as the piece being worked out, itself included, or how many of them the filter matches. Same rules as `hexsum`.

```formula
hexcount("marker(\"Type\") == \"Engineer\"") > 0
```

True when the stack has an engineer.

### hexmax(Property) or hexmax(Property, "filter")

The largest value of the property among the pieces in this piece's hex (the filter, if given, narrows them). Same rules as `hexsum`; when no piece matches it is an error, `No piece matches "{filter}"`.

```formula
hexmax(Readiness, "marker(\"Kind\") == \"Unit\"")
```

The best Readiness in the stack.

### hexmin(Property) or hexmin(Property, "filter")

The smallest value of the property among the pieces in this piece's hex. Same rules as `hexmax`.

```formula
hexmin(Ammo, "marker(\"Kind\") == \"Unit\"")
```

The lowest ammo of the units in the hex. The filter matters: without it, every other piece in the hex (a marker, a fort) counts too, with Ammo's Default.

### attachedsum(Property) or attachedsum(Property, "filter")

The total of a property over the pieces on the organisation chart of the piece being worked out, such as the units attached to an HQ; give a filter to count only some of them. The chart is the map named by the piece's first **Send to location** (VASSAL SendTo) trait whose map is an organisation display (a map the module shows as an org chart). Only pieces physically on that chart count: a unit of the formation that stands on the main map is not attached. The piece itself counts when it is on the chart too (a filter can leave it out), and it does not matter where the piece itself stands: an HQ on the main map still reads its chart. Same rules as `hexsum`: it needs a piece, elsewhere it is `attachedsum() is not available here`, and a phase condition may not use it. A piece with no such trait is an error, `{piece} has no organisation chart`.

```formula
attachedsum(Fuel, "marker(\"Kind\") == \"Unit\"")
```

All the fuel of the units on this HQ's organisation chart.

### attachedcount() or attachedcount("filter")

How many pieces are on the organisation chart of the piece being worked out, or how many of them the filter matches. Same rules as `attachedsum`.

```formula
attachedcount("marker(\"Kind\") == \"Unit\"") == 0
```

True when the HQ has no units left on its chart.

### attachedmax(Property) or attachedmax(Property, "filter")

The largest value of the property among the pieces on this piece's organisation chart. Same rules as `attachedsum`; when no piece matches it is an error, `No piece matches "{filter}"`.

```formula
attachedmax(Readiness, "marker(\"Kind\") == \"Unit\"")
```

The best Readiness of the units attached to this HQ.

### attachedmin(Property) or attachedmin(Property, "filter")

The smallest value of the property among the pieces on this piece's organisation chart. Same rules as `attachedmax`.

```formula
attachedmin(Ammo, "marker(\"Kind\") == \"Unit\"")
```

The lowest ammo of the units attached to this HQ.

**Piece sums and hidden information.** In a formula, `sum`, `countof`, `maxof`, `minof` and the `hex` and `attached` ones read every piece and every value the rules need, hidden or not, so a formula gives the same result whoever runs it. In hot-seat and online play, a player who does not hold every side cannot be shown such a result when it may depend on something they cannot see. For that player a piece sum counts as reading hidden data when:

- it is a count (`countof`, `hexcount`, `attachedcount`);
- its property is Private, or its filter reads a Private property, calls `holder()` or another piece sum, or does not parse;
- or some pieces may be hidden from that player at all: a map or organisation display belongs to a side they do not hold (the Visibility tab of the Rules Workbench), or a piece of the module, including one placed by a predefined setup, can be hidden or masked (a VASSAL Invisible or Mask trait). Then every piece sum counts, even of a Public property.

A formula that uses such a piece sum (in its text, an output, a precondition, an "Only when", an auto value or a table key), or reads a computed property built from hidden data (see "Computed properties" below), does not show its working or errors to that player; a guest's Formula dialog leaves the calculation to the host, and an "Only when" or precondition that uses one is decided by the host. A sum, maximum or minimum of a Public property whose filter reads only Public values, in a game where no piece can be hidden, is shown as usual.

Warnings, alerts and Ledger filters are worked out on what each player can see. For a player who does not hold every side, one that uses such a piece sum does not match (in a formula run on that player's own screen, the piece sum is the error `sum() reads data this player cannot see`). A player holding every side sees them as usual.

An output still writes its result where the formula says: if a formula writes a piece sum (or anything worked out from one) into a Public property, every player sees that value, and so learns the total. Write such results into a Private property, or a holder of the side that owns them, when the total should stay hidden.

### hex()

The name of the hex the counter is in, as text, such as `"0412"`: the same name a Location binding and `hexsum` use. It takes no arguments.

```formula
keyed(FordDepth, hex())
```

The ford depth of the hex this counter stands in. Run with no counter it stops with `hex() needs a unit`; on a counter that is not on a grid, with `Raft A is not in a hex`.

### distance(a, b)

The number of cells between two hexes of the same board, as a number. Both arguments must be hex names as text: a name such as `0412` would lose its zero as a number, so a number stops the run with `distance() needs hex names as text`. Hex grids count hex steps; square grids count king moves, as the range tool does. `distance(hex(), hex())` is 0.

```formula
distance(hex(), "0507") <= 3
```

True when this counter is within 3 hexes of the lighthouse at 0507. The board of the counter is tried first, then every board of the module. A name no board has stops the run with `distance(): no hex named "0999" on any board`; two names on different boards, with `distance(): "0412" and "0507" are not on the same board`; a board with another kind of grid, with `distance() needs a hex or square grid`. A phase condition, a property's Applies to, a side's unit filter and a starting value cannot use `hex()` or `distance()`.

### keyed(Property, key) or keyed(Property, key, Holder)

The value of a **keyed** property for one key. The property (and the holder) are bare names, not variables. A keyed property keeps one value per key instead of one value (see [Places and keyed values](Making-a-Rules-Pack#places-and-keyed-values)), so a plain name or a binding cannot read it: `"FordDepth" is keyed; read it with keyed()`.

```formula
keyed(BargesDue, turn())
```

The barges due this turn. A key never written reads the property's Default. Whose value it reads: with a third argument, that holder's (`keyed(HullCount, "Barge", Boatyard)`; the holder must keep the property, and is written as its name in identifier form: letters, digits and `_`); for a Pieces property, the counter's own; for a Holders property, the global holder's; for a Both property, the counter's when there is one, else the global holder's.

The key is checked as an output's **Key** is: a table-keyed property takes a row of its table, a turn-keyed one a whole number from 1 to 100000, a hex-keyed one a hex of a board; at most 64 characters. A bad key stops the run with `keyed(): no row "Sloop" in table "Hull types"`, `keyed(): the turn must be a whole number of at least 1`, `keyed(): no hex named "9999" on any board` or `keyed(): the key is too long`. The other messages are `keyed() needs a property name`, `keyed() needs a holder name`, `Unknown property 'x'`, `FordDepth is not keyed` and `keyed() needs a unit`.

The piece sums (`sum`, `maxof`, `minof` and the `hex` and `attached` ones) cannot total a keyed property: `sum() cannot total keyed property "FordDepth"`.

**Hidden information.** In a player's view, `keyed()` of a value that player may not see (a Private place property, a counter property of a side that player does not hold, or a holder of such a side) stops with `keyed() reads data this player cannot see`, so the host works it out, as for a piece sum.

### askside()

The name of the side the run asks: in a run a start entry asks of one side, or in a sealed run (see [Making a Rules Pack](Making-a-Rules-Pack)), the side's name as `side()` gives it (its id when the name is blank). It is the run's side even when an ally answers it (see [Alliances](Making-a-Rules-Pack#alliances)). Anywhere else it is empty text. It is never an error.

```
askside()
```

For example, a sealed bid written with the key `askside()` keeps one value per side in a property keyed by a table of the side names, such as `Northern League` and `Southern Compact`.

## What a phase condition may read

A phase can have a **Runs only if** condition (see [Making a Rules Pack](Making-a-Rules-Pack#phase-conditions)). A condition is checked when Quartermaster is about to start the phase, with no unit in hand, so it may read only:

- global properties (a global property that has not been set reads its Default);
- tables, through `lookup`;
- `turn()`, `stage()` and `segment()` of the position being tested;
- `phaseside()`, the name of the side whose phase is being tested;
- global keyed values, through `keyed()` (on entry to a phase the keys of past turns are already dropped, so a victory condition checked there reads `keyed(BargesDue, turn())` but no longer an earlier turn's key); `hex()` and `distance()` are not available (no counter);
- constants and the pure functions (`if`, `min`, `max`, `round`, `floor`, `ceil`, `abs`, `clamp`, `mod`, `startswith`, `contains`).
- the whole-game piece sums `sum`, `countof`, `maxof` and `minof` (their filters test each piece, so they may read pieces, and even use the `hex` ones: `countof("hexcount() > 1") > 0` is true when any hex holds a stack).

A **victory condition** (the End of game tab) is read as a phase condition, with the same rules: it is checked on the host's full game, once when the game enters a phase it names and once at the end of the last turn, for example `NorthPorts >= 4` (see [The last turn and victory](Making-a-Rules-Pack#the-last-turn-and-victory)). A condition that fails counts as not met.

It may not use `name()`, `marker()`, `has()`, `side()`, `map()`, `holder()`, `runs()`, `dice()`, the `hex` and `attached` functions (outside a piece sum's filter) or the values of a piece or a side's holder. A formula that fails never skips the phase: the phase runs and a notice tells you what went wrong.

## Where names get their values

A bare name in a formula (`Fuel`, `Hexes`, `Weather`) is a **variable**. How it gets its value depends on where the formula lives.

**In a formula on the Formulas tab**, each variable has a row under **Bindings** that says where its value comes from. The kinds are:

| Kind | Where the value comes from | Example |
|---|---|---|
| Unit | a property of the counter the formula is run on | Fuel |
| Global | a property kept on the global holder | Weather |
| Holder | a property of a named holder, such as a depot, or, with **Holder choices**, of the holder the player chooses | Stores of North Depot |
| Table | a cell of a table, chosen by a row (and column) expression | Gain, row Hexes |
| Location | the name of the hex or zone the counter stands on (text) | 0302 |
| Input | a value the player types in the Formula dialog, with limits, choices and an automatic value for hands-off play | Hexes |
| Roll | a dice roll such as 1d6 | Die |
| Moved | the distance the counter just moved, in hexes (known only when it is moved) | Moved |
| Piece | a property of another counter, which the player picks from a list when the formula runs | Speed of the target |
| Draw | members drawn at random from a group of counters (the value is how many were drawn) | Hits |

Every name used in the formula needs a binding, and every binding should be used. A name that appears in another binding's automatic value (for example `Hexes` in `auto: Hexes + 1`) counts as used when that other binding is itself used, but a binding's own name does not count as using itself; the Workbench's Problems list tells you when they do not match. The longer explanation, with pictures of each case, is in [Making a Rules Pack](Making-a-Rules-Pack#step-4-formulas). How the answers are shown and applied is in the [Formula dialog](Window-Formula-Dialog).

**Choices from a table (Input bindings).** An Input can take its choices from a table instead of a list typed in Details: set its **Choices table**, and optionally a **Choices filter** (an expression, true for each row key to offer; see [`row()`](#row)). An Input with a choices table cannot also have typed choices. Each counter gets its own list; with no counter the list holds every key. See [Choices from a table](Making-a-Rules-Pack#choices-from-a-table).

**Rolling once for a group.** A formula with **Roll once for the group** ticked shares one set of dice between the counters of a group (the same hex and side in a phase entry; the rows of a menu batch); the Roll box of a shared roll reads `(group roll)` and its tooltip says `Rolled once for the group`. See [Rolling once for a group](Making-a-Rules-Pack#rolling-once-for-a-group).

**Reading another counter (Piece bindings).** A formula reads the counter it runs on; a **Piece** binding reads a property of a second counter instead, chosen by the player each time the formula runs. Its Target is the property (one that pieces hold: scope Pieces or Both; a stored or computed property) and its Details are the **which pieces** filter, a unit filter such as `startsWith(name(), "Blue")` (blank = every piece).

- **Same hex.** Tick **Same hex** on the binding to list only the other counters in the hex of the counter the formula runs on (never that counter itself). With nobody else there the message is `No piece in this hex`; run from nowhere (no counter) it is `Needs a unit`. A formula whose only link to a counter is a Same hex binding is offered on counters in the counter menu, like one with a Unit binding. It only works for counters on a grid.
- **Picking.** In the Formula dialog the binding is a list of every counter the player can see that matches the filter. Each entry reads `{name} ({location})`, or just `{name}` for a counter that is not on a grid. Two entries with the same text get ` #2`, ` #3` and so on, in the order the counters were placed (the first keeps the plain text); the list is sorted by that text, but the counters in the same hex as the counter the formula runs on come first, so a transfer to a neighbour on the same hex is the first thing offered. A masked enemy counter shows as `Unknown piece` (with its location, if it has one), never its real name. The value is read when the formula runs, like a Unit binding: the property's default when it has no value yet, and no value for a computed property that does not apply to that counter.
- **The pick follows the counter.** Once picked, the entry stays with that counter while the dialog is open: if the list changes (say an earlier counter with the same text is removed, so `Rifle (0302) #2` becomes `Rifle (0302)`), the box shows the counter's new text. If the picked counter leaves the list, the box is cleared and the formula waits for a new pick; it never moves to another counter. On a shared (hot-seat) screen, a new player at the screen starts with no picks.
- **Always asked.** A Piece binding is never filled in automatically, so Fully automatic stops and opens the formula for each unit so you can pick (the formula dialog for one unit, the batch grid for several; in the grid the Piece column is a list too, the same for every row) instead of asking once for the whole run. The working line names the counter, such as `Speed of Blue Lancers`.
- **Errors.** Text that is not on the list, or a pick that is no longer on it when the formula runs (for example a counter moved, deleted or hidden before the host ran a player's formula), is `Choose one of the listed pieces`, and nothing is changed. With no counter to pick, the message is `No piece matches "{filter}"` (with a blank filter, `No piece to choose`).
- **Hidden information.** When the property is not Public (Private, or computed from hidden data), a player who does not hold every side still picks the counter, but its value is hidden from them: the box keeps the pick and says the value is hidden, an online guest's dialog leaves the calculation to the host, and the formula's working is not shown to players who do not hold every side. A Public property is shown as usual, unless that player's view may lack counters or values (a map of another side, or counters that can be hidden or masked): then a pick may be a masked counter, so its value is hidden in the same way. The list only ever holds counters that player can see.
- **Workbench.** The Problems list reports `Formula "{f}": variable "{v}" reads unknown property "{p}"`, `Formula "{f}": variable "{v}" reads holder property "{p}"` and `Formula "{f}": variable "{v}" "which pieces" filter does not parse: {message}`.

An invented Riverland example: a raider slips past a defender when it is faster. The formula `Speed - Target` runs on the raider, with `Speed` a Unit binding of Speed and `Target` a Piece binding of Speed whose which-pieces filter is `startsWith(name(), "Blue")`. When the formula runs, the player picks the defender from the list of Blue counters they can see; a positive result means the raider gets through.

```formula
Speed - Target
```

**Choosing a holder (Holder choices).** A Holder binding with **Holder choices** (holder names separated by commas) is a picker: the player chooses one of the listed holders each time the formula runs, and the value is the binding's property on the chosen holder. Only holders the player may act on now are offered (no owner, or owned by a side the player holds or an ally of one), so the chosen holder's values are always visible to the chooser. The binding is always asked, and its working reads `Stores of Ford Dump`. An output with Target Holder and a **Holder variable** writes the chosen holder. The global holder cannot be listed. See [Choosing a holder when the formula runs](Making-a-Rules-Pack#choosing-a-holder-when-the-formula-runs).

**Drawing at random (Draw bindings).** A **Draw** binding draws members at random from a group of counters, without the player choosing. Its Target is the property giving each counter's number of members (the weight, a whole number from 0 to 1000; `(one each)` = one member each), its Details are the pool filter (a unit filter; blank = every counter), **Same hex** limits the pool to the hex of the counter the formula runs on (that counter included), and **Draw count** is an expression giving how many to draw (a whole number from 0 to 1000, which may read earlier variables but not this one). The variable's value is k = the count, or fewer when the pool has fewer members. Each step is one die with as many faces as members remain, drawn by the engine without replacement. An output with Target Piece and Piece set to the Draw variable acts once on each counter that gave members; inside its Value the variable reads that counter's share. Place and Reveal cannot act on a draw. See [Random draws among counters](Making-a-Rules-Pack#random-draws-among-counters).

**In a filter** (a formula's Applies to, a property's AppliesTo, a side's unit filter, an alert's condition, or a Ledger tab's filter), there are no bindings. A bare name means the **property of that name on the unit being tested**. If the unit has no value for it yet, the property's **default** is used, as long as the property applies to that unit (its scope includes units, and its own Applies to, if it has one, matches). `has(Readiness)` still means "this unit has a value in the ledger", so it stays false until a value is set. If the property does not apply to the unit, that is an error, so guard with `has(...)`. In a player's view of the game (hot-seat view or an online guest), a value may be hidden rather than unset, so private values, and every value of another side's masked unit, do not fall back to their default there. The functions `name()`, `marker("...")`, `side()` and `map()` read the unit too. `lookup` and `holder()` work in filters; `dice` does not.

**Bare names for global values.** In a filter, a bare name of a property whose scope is **Holders** reads the **game-level** (global) value, not a unit's. Names of Pieces or Both properties still read the unit. This is how a filter such as `Weather == 2` works: it is true or false for the whole game. In a rare case this changes an old pack: a property moved from scope Both to Holders that still has values on pieces now reads the global value in a filter, not the piece's.

**A filter that never reads a unit shows once.** A phase warning or an alert whose condition reads no Pieces or Both property and uses none of `name()`, `marker()`, `has()`, `side()` or `map()` is a **game-level** rule. It is checked once, not once per unit. A game-level warning shows as one line in the phase panel with no units under it: its text is the message, or the condition text if the message is empty. A game-level alert has one match, on the global holder: its text is the message, or the alert's Name if the message is empty. A game-level decision reads just its text, with no "— 0 units". Text left over at the start of the message when `{unit}` is removed, such as the ": " of "{unit}: set up", is dropped. The Workbench notes a game-level warning or alert whose message uses `{unit}`, since that name is left out.

This changes some older packs. A warning or alert whose condition reads only constants, `turn()`, `stage()`, `mod`, or `lookup` on those used to list every unit, one row each, and now shows one line. A game-level alert with a Side no longer applies that side's unit filter. To get one row per unit back, add a unit read to the condition, for example `and name() != ""`.

```formula
has(Readiness) and Readiness >= 3
```

True for units that have Readiness 3 or more.

## Outputs

A formula's **Outputs** say what its result changes. Optional columns in the outputs grid change what each output does: **Value**, **Choose from** and **Pick by** (below), and **Action**, **Piece**, **Palette entry**, **Layer** and **To map** ([Acting on other counters](#acting-on-other-counters)).

**A value per output.** Normally every output gets the formula's result. Type an expression in **Value** and that output gets its own value instead, worked out in the same run (the same dice, inputs and bindings as the text). The formula's result is not a variable there. A blank **Value** means the formula's result. The working line shows each output's own value, such as `Fuel 140 - 12 = 128; Ammo 30 - 6 = 24`. If the value is not a number the run stops with `The value for Fuel is not a number`.

An invented example: a convoy loses supplies to one roll of `Loss` (a Roll of `1d6`). The text is `Loss`, the Fuel output keeps a blank Value, the Ammo output has Value `Loss * 2` and the Rations output has Value `max(Loss - 1, 0)`. One roll then takes from three supplies, and no scratch property is needed.

**A property picked at run time.** In **Choose from**, list properties separated by commas (for example `Fuel, Ammo, Rations`), with a name that holds a comma in "double quotes" and give **Pick by** an expression. A number picks the Nth property in the list (1 is the first); text picks the property with that name (capitals and spaces at the ends do not matter). Anything else stops the run with `Medicine is not one of the output's properties`. With a list but no **Pick by**, the Workbench reports `Formula "Raid": output 1 has a property list but no "Pick by"`. An empty list means the output's own **Property** is used. If you delete a property that a list names, the list keeps it (so a number pick still means the same position) and the Workbench reports `Formula "Raid": output 1 refers to a property that no longer exists` until you fix the list.

For example, an Input `Slot` of `1..3` and Pick by `Slot` lets the player choose which of Fuel, Ammo or Rations the output changes, with one formula instead of three. Values and the picked property are all worked out before anything is written, so a run that fails changes nothing.

### Acting on other counters

**Target** says whose value an output writes: **Unit** (the counter the formula runs on), **Global**, **Holder**, **Piece** (the counter a Piece binding picked; name the binding in the **Piece** column) or **Placed** (the counter this formula's Place output just put on the map). **Action** says what the output does; blank means Write.

| Action | Does | Targets | Value |
|---|---|---|---|
| Write (default) | Writes a property, as every output always did | Unit, Global, Holder, Piece, Placed (Placed: Op Set only) | the number written |
| Place | Puts the counter named in **Palette entry** (a path in the module's palette, such as `Markers/Wreck`, or its gpid) in a hex, on top of that hex's stack | Unit (this counter's hex), Piece (the picked counter's hex) | not used |
| Remove | Deletes the counter, or with **To map** moves it to the middle of that map's first board and keeps its ledger values | Unit, Piece | not used |
| Set layer | Sets the layer named in **Layer** to the level (1 = first level, 0 = off) | Unit, Piece, Placed | the level, a whole number |
| Set mask | Masks (1) or unmasks (0) the counter | Unit, Piece, Placed | 1 or 0 |
| Reveal | Shows one property's value in the log, exactly or as a range (below); writes nothing | Unit, Piece, Holder, Global | not used; **Margin** gives the range's width |

How a run with these outputs behaves:

- **One step.** The whole run, placements, writes, layer and mask changes and removals, is one undo step.
- **Order.** Whatever order the outputs are listed in, a run does: rolls, the placement, writes to the counter itself, a global or a holder, writes to a picked counter, writes to the placed counter, layer and mask changes, and last the removals. So a value is always written and logged before its counter goes.
- **Only one placement.** A formula may have one Place output. Outputs that write the placed counter come from the same formula and must be **Set**: a new counter starts from the property's default.
- **The placed counter's own values.** It is seeded as any new counter is (its sheet values and starting values), and then the Placed outputs run, so a Set overrides the seed.
- **The picked counter is asked for.** A Piece output uses the counter the player picked in the Piece binding, so it is always asked (Fully automatic stops for it). A Piece output may act on a counter of any side, including one you do not hold; an online guest's formula that has a Piece output is worked out by the host.
- **A drawn counter.** An output whose Piece is a Draw variable runs once on each drawn counter, in the order of the counters' ids, each write its own ledger line stamped with that counter's side; the log first gives one `{formula}: {Var}: {n} on {name}` line for each drawn counter. Before Apply nothing says which counters were drawn: each such output previews as `{Prop} of the drawn counters changes`. See [Random draws among counters](Making-a-Rules-Pack#random-draws-among-counters).
- **A chosen holder.** An output with Target Holder and a Holder variable writes the holder the player chose (the property must be kept by every listed holder).
- **The log.** A write to a picked counter is logged on that counter's side: its owner sees `{counter}: {formula}: Strength 7 - 2 = 5`, the side that ran the formula sees `{Side} ledger changed`, and a counter of no side is public. The effects read `{formula}: placed {name}`, `{formula}: removed {name}`, `{formula}: moved {name} to {map}`, `{formula}: {layer} of {name} = {level}` and `{formula}: {name} masked` or `{formula}: {name} unmasked`. A line is dropped for a player who cannot see that counter.
- **The working** on the formula's own lines leaves out the parts about a picked counter (the owner sees them on their line), and names an effect's counter as the player sees it: `Unknown piece` for a masked counter.

Examples are in [Acting on other pieces](Making-a-Rules-Pack#acting-on-other-pieces).

#### Reveal and Margin

A Reveal output names a **Target** (not Placed) and one **Property**, and takes no **Value**, no property list and no keyed property. It reads the value when the run happens, in output order (after the run's earlier writes to the same counter and property), and logs one public line. **Margin** is an expression for the width of the range; blank or 0 shows the exact value. A margin above 0 draws a window that always holds the true value: with value `v` and margin `m`, one die of `m + 1` faces gives `k`, the window is `v - k` to `v - k + m`, and a window that would start below 0 for a value of 0 or more is shifted to `0` to `m`. The line reads `{subject}: {Property} {value}` (margin 0) or `{subject}: {Property} between {low} and {high}`; the subject is the counter's name as the player sees it, the holder's name, or `Global`. The window's die is never logged, and nothing of the value shows before Apply (the working says `reveal {Property}`). The margin must come to a whole number from 0 to 1000, otherwise the run stops with `The margin for Strength must be a whole number from 0 to 1000`. Keep the margin to visible data or constants; see [Revealing a value](Making-a-Rules-Pack#revealing-a-value) for the cautions.

## Computed properties

A property can hold an **Expression** (the Expression column of the [Properties & Holders tab](Window-Rules-Workbench#properties--holders-tab)). It is then **computed**: its value is the expression, worked out every time the property is read, and never stored. Use it for a value that follows from others, so the expression is written once instead of being pasted into every formula, warning and filter. An empty Expression means an ordinary stored property; packs without expressions behave exactly as before.

An invented example: Riverland's units could have a property **Range** with the expression `Fuel * 2 + Readiness`. A formula binding, a filter such as `Range >= 6`, a warning, `sum(Range, "...")` and the Ledger all read the current value; when Fuel changes, Range changes with it.

- **What the expression reads.** A Pieces or Both property's expression is worked out for the piece, as a filter reads it: that piece's properties, global values, `name()`, `marker()`, `lookup`, the piece sums and the `hex` and `attached` functions (`attachedcount("marker(\"Kind\") == \"Unit\"")` gives each HQ the number of units on its chart). A Holders property's expression is worked out for the holder: that holder's properties and global values. A computed property may read other computed properties.
- **Read-only.** The Ledger shows the value, with `= {expression}` in its tooltip, and refuses to edit it: `Range is computed (= Fuel * 2 + Readiness) and cannot be edited`. Formula outputs may not write it, and its Default and SheetField are ignored (the Workbench greys them).
- **Where it does not apply.** On a piece its Applies to does not match, a computed property has no value (`has(Range)` is false there). A piece sum skips such pieces instead of counting a Default: `sum` and `countof` over none of them give 0, and `maxof` or `minof` over none is `No piece matches "{filter}"`.
- **Errors.** An expression that fails when read (a division by zero, a missing value, `{piece} has no organisation chart`) is an error named after the property, such as `Range: Division by zero`. The Ledger shows `error` in the cell with the message as its tooltip; a formula, warning or filter treats it as it treats any other error. It never reads as 0.
- **Cycles.** A property that reads itself through other computed properties (Range reads Reach, which reads Range) is an error in the Workbench, `Property "Range" is computed from itself: Range → Reach → Range`. If one slips through anyway, or computed properties nest more than 16 deep, reading it is the error `Range is computed from itself`.
- **Hidden information.** A computed property counts as **Private**, whatever its Visibility says, when its expression reads a Private property (directly or through another computed property), calls `holder()`, or uses any piece sum or `attached` function: a player who does not hold every side sees it as Private, as in "Piece sums and hidden information" above. The Workbench warns when such a property is set to Public, and the Visibility tab marks it "(computed from hidden data)". A formula that reads such a property, through a unit, global or holder binding (even of that player's own unit or the global holder) or `holder()`, is treated like one using a hidden piece sum: its working and errors are not shown to that player, a guest's Formula dialog leaves that value to the host, and an "Only when" that depends on it is decided by the host. A computed property set to Private has no value on the pieces and holders of a side that player does not hold, even when everything it reads is Public, just as a stored Private value is not shown there. For this the views must know whose pieces are whose, so while the pack has a computed property that counts as Private, a side's unit filter may not read a Private property (`Side "Red": unit filter may not read Private property "Morale" while the pack has Private computed properties (players' views could not tell whose pieces they hide)`).

## A piece's own value for a table rating

Some rules change a printed rating for one counter only: an upgraded unit moves faster than its row of the chart says. Instead of an extra property and an `if` around every `lookup` of that column, give the property an **Overrides table** (and, for a table with two dimensions, a **Column**) on the [Properties & Holders tab](Window-Rules-Workbench#properties--holders-tab). Then every read of that column, at the row named like the piece, gives the piece's own value instead of the printed cell.

An invented Riverland example: the table `Rafts` has the rows `Raft A` and `Raft B` and the columns `Speed` and `Load`; Raft A's printed Speed is 3 and Raft B's is 5. The property **Upgraded Speed** (scope Pieces) overrides `Rafts`, column `Speed`. Raft A has Upgraded Speed 6 in the Ledger; Raft B has none.

```formula
lookup(Rafts, name(), "Speed")
```

Run on Raft A this gives 6; run on Raft B it gives 5, the printed cell. A Table binding of `Rafts` with row `name()` and column `"Speed"` gives the same values, and so do a filter, a warning and an alert worked out for that piece.

- **When the piece's value is used.** Only when all of these hold: the table and column are the ones the property overrides (a one-dimension table has no column, so any read of it counts); the row found is the piece's own name, compared as row keys are compared (capitals and extra spaces do not matter, and a numeric row key such as `12` matches the name `12.0`); and the piece **has** a value for the property: a value in the Ledger, or a computed property that applies to it (`has(Upgraded Speed)` is true). Otherwise the read gives the printed cell. The property's Default is never used.
- **Other pieces' rows.** `lookup(Rafts, "Raft B", "Speed")` run on Raft A gives Raft B's printed 5, even if Raft B has an Upgraded Speed: only the piece being worked out uses its own value. A read with no piece (a phase condition, a holder's computed property, a formula run with no counter) gives the printed cell. `lookuprange` always reads the printed cells.
- **Several properties.** When two properties override the same column, the first in the pack's order that the piece has a value for wins; the Workbench warns.
- **Working.** A Table binding's source in the Formula dialog shows the override and the printed value: `Rafts, row Raft A, column Speed: Upgraded Speed of Raft A (printed 3)` (one dimension: `Costs, row Raft A: Cheap of Raft A (printed 5)`). An inline `lookup` shows the value as usual. The Ledger tooltip of the property adds a line `overrides Rafts, column Speed` (one dimension: `overrides Rafts`).
- **A computed override reading its own cell.** While a piece's override value is being worked out, every read of that cell for that piece reads the **printed** value. So **Upgraded Speed** can be computed as `lookup(Rafts, name(), "Speed") + 2`: on Raft A it gives the printed 3 + 2 = 5, and every other read of that cell then gives 5. Reading **Upgraded Speed** itself (a Unit binding, a filter such as `Upgraded Speed == 5`, `sum(Upgraded Speed, "")`, the Ledger) gives the same printed 3 + 2 = 5. Other cells, and other pieces' overrides, are read as usual.
- **Do not read the cell through another computed property.** Suppose **Base Speed** is computed as `lookup(Rafts, name(), "Speed")` and **Upgraded Speed** as `Base Speed + 2` overrides `Rafts`, column `Speed`. Reading the table still works (Upgraded Speed is worked out, and its Base Speed reads the printed cell), but reading **Base Speed** on its own reads the table, which is Upgraded Speed, which reads Base Speed again: the error `Base Speed is computed from itself`. Read the cell inside the override's own expression instead: `lookup(Rafts, name(), "Speed") + 2`.
- **Hidden information.** When the overriding property is not Public, a player who does not hold the piece's side cannot see its value, so reading that table counts as reading hidden data, exactly as a Unit binding of a Private property does: in a formula run on a counter of a side that player does not hold (or a counter they cannot see), a Table binding of that table and every `lookup` of it are hidden, the Formula dialog shows `hidden — the host calculates` to a guest, the working is not shown, and an "Only when" or precondition with a `lookup` is decided by the host. On the player's own counters nothing is hidden. A Public computed property whose expression looks up such a table counts as Private, and a piece sum whose filter looks one up counts as reading hidden data.

**A key for a keyed property.** When an output writes a keyed property it also needs a **Key**, an expression worked out after the value: `hex()` for the counter's hex, `turn() + 2` for two turns ahead, or a table row in quotes such as `"Barge"`. The working line names the key: `FordDepth[0412] = 3 (Flooded)` or `BargesDue[5] 1 + 2 = 3`. A failure reads `The key for BargesDue: the turn must be a whole number of at least 1` (the same texts for a table row, a hex and a long key). **Key** is closed for an action other than Write. See [Places and keyed values](Making-a-Rules-Pack#places-and-keyed-values).

## Common errors

The Workbench shows these in the Problems list and next to the box you are typing in. The Ledger shows a filter error in a banner above the table. Errors found while a formula runs appear in the Formula dialog.

### Errors in how a formula is written

Most of these end with "at position N", where N counts characters from the start of the formula (the first character is position 0).

| Message | What it means | How to fix it |
|---|---|---|
| Formula is empty | The box holds nothing, or only spaces. | Type a formula. |
| Formula is too long at position 10000 | More than 10,000 characters. | Split the work across two formulas. |
| Formula nests too deeply | More than 200 levels of brackets and calls. | Simplify, or move part into its own variable. |
| Unexpected 'x' | A word, sign or bracket in a place it cannot go, such as two values side by side or a stray `)`. | Look just before the position; usually a sign or comma is missing. |
| Unexpected character 'x' | A character the language does not have, such as `%`, `^`, `;` or a single quote. | Remove it. Use double quotes for text. |
| Unexpected '=' (use '==' to compare) | A single `=` was typed. | Write `==` to compare. A formula cannot assign. |
| Unexpected '!' (use 'not' or '!=') | A `!` on its own. | Write `not x` or `a != b`. |
| Formula ended unexpectedly | The formula stops where a value was expected, such as `Fuel +`. | Add the missing value. |
| Expected ')' | A bracket or a function call is not closed, or two arguments lack a comma. | Add the `)` or the comma. |
| Comparisons can't be chained | Two comparisons in a row, like `1 < a < 5`. | Join them with `and`. |
| Bad number '1.2.3' | A number the reader cannot understand. | Fix the digits. |
| Text is missing its closing quote | An opening `"` with no matching `"`. | Add the closing quote. |
| Unknown escape in text (use \" or \\) | A backslash followed by something other than `"` or `\`. | Write `\\` for one backslash. |
| Unknown function 'x' | A name followed by `(` that is not one of the functions on this page. | Check the spelling against the list above. |
| 'min' takes at least 2 arguments, not 1 | Wrong number of arguments. The message names the function and what it takes: `1 argument`, `3 arguments`, `1 to 2 arguments` or `at least 2 arguments`. | Give the function the right number. |
| lookup needs a table name | The first argument of `lookup` is not a bare table name (for example a number or quoted text). | Write the table's name without quotes. |
| sum needs a property name / sum needs a text filter | The property of `sum`, `maxof`, `minof` or a `hex` function is not a bare name, or its filter is not quoted text (the message names the function). | Write `sum(Fuel, "...")`: the property bare, the filter in quotes. |

### Errors while a formula runs

| Message | What it means | How to fix it |
|---|---|---|
| '+' needs numbers, got text | A sum or comparison was given text or true/false. The message names the operator (or function) and what it got: `text` or `true/false`. | Check which variable holds text. `==` and `!=` are the only operators that take text. |
| '-' needs a number, got text | The minus sign in front of a value that is not a number. | Remove the sign or use a number. |
| 'and' needs true/false or a number, got text | `and`, `or`, `not` or `if` was given text. | Compare first: `marker("Kind") == "Unit"` gives true/false. |
| 'startsWith' needs text, got a number | `startswith`, `contains`, `marker` or `has` was given a number or true/false. | Put the value in quotes, or compare instead. |
| 'dice' count must be a whole number from 1 to 100 | Also `'dice' sides must be a whole number from 2 to 1000` and `'round' digits must be a whole number from 0 to 10`. | Use a whole number within the range. |
| 'clamp' low limit is above the high limit | The second argument of `clamp` is bigger than the third. | Swap them. |
| Division by zero | The right side of `/` was 0. | Guard it: `if(Hexes == 0, 0, Fuel / Hexes)`. |
| Result is not a finite number | A sum overflowed. | Check for huge values. |
| The margin for Strength must be a whole number from 0 to 1000 | A Reveal output's **Margin** came out as text, a fraction, a negative number or more than 1000 (the message names the property). | Use a whole number from 0 to 1000, or leave Margin blank for the exact value. |
| No table named 'x' | A `lookup` names a table that does not exist. | Check the spelling against the Tables tab. Write the name as it appears on the Tables tab (capitals don't matter); use one word. |
| No row for 5 in SupplyByDistance | No row key in the table matches the value (the message shows the value and the table). | Add a row, or an open-ended row such as `5+`. |
| No column of Landing row 2 holds 7 | `lookuprange` found the row, but no cell of it holds the value (the message shows the table, the row and the value). | Add a cell that holds it, such as an open-ended `6+`. |
| lookuprange needs a two-way table | `lookuprange` was given a table with one dimension. | Use a table with rows and columns, or use `lookup`. |
| count() needs a roll binding, not Fuel | Validator: `Formula "{name}": count() needs a roll binding, not {variable}`. The first argument of `count` is not a Roll binding. | Make that variable Kind Roll. |
| count() needs each die of Planes: type them separated by spaces | Physical dice: a single number was typed for a roll that `count` reads. | Type every die, such as `1 2 5 6`. |
| Planes needs 4 dice from 1 to 6 | Physical dice: the typed list has the wrong number of dice, or a die outside the range. | Type exactly that many dice, each within the range. |
| 'Planes' dice count must be a whole number from 1 to 100 | A Roll binding's **Dice count** expression gave something else. | Make the expression give a whole number from 1 to 100. |
| lookuprange() is not available here / count() is not available here | The function was used where it cannot run (`count` outside a formula run). | Move it into a formula with a Roll binding. |
| hexsum() is not available here | `hexsum`, `hexcount`, `hexmax` or `hexmin` (or `attachedsum` and the other `attached` ones) was used where no piece is being worked out, such as a phase condition or a formula run with no counter. | Run it on a unit, or use `sum`, `countof`, `maxof` or `minof` for the whole game. |
| HQ North has no organisation chart | An `attached` function ran on a piece with no Send to location trait to an organisation display (the message names the piece). | Run it on an HQ, or guard the formula's Applies to. |
| Range: Division by zero | Reading the computed property Range failed (the message names the property, then the error). | Fix the property's Expression, or guard it, such as `if(Hexes == 0, 0, Fuel / Hexes)`. |
| Range is computed from itself | A computed property reads itself through other computed properties, or they nest more than 16 deep. | Break the circle in the Expressions. |
| Rifle is not in a hex | A `hex` function ran on a piece that has no grid location (the message names the piece). | Run it on a piece that is on a gridded map, or guard the formula's Applies to. |
| sum() reads data this player cannot see | A piece sum ran on the screen of a player who does not hold every side, and it may read pieces or values that player cannot see (see "Piece sums and hidden information" under the functions above). | Nothing to fix: the host (or a player holding every side) works it out. In a warning, alert or Ledger filter it simply does not match. |
| No piece matches "Fuel > 3" | `maxof`, `minof`, `hexmax`, `hexmin`, `attachedmax` or `attachedmin` found no piece (the message shows the filter). | Guard it: `if(countof("...") > 0, maxof(...), 0)`. |
| No column for x in Table | The same, for the column of a two-dimension table. | Add a column that matches. |
| Table T needs a column | `lookup` was given only a row, but the table has two dimensions. | Give a column as the third argument. |
| This formula needs a unit | `name()` or `marker(...)` was used in a formula that was run with no counter. | Run the formula from a counter, or choose a Test piece in the Preview. |
| No marker 'x' | The counter has no marker with that key. | Check the key. Riverland's keys are Side, Kind and Type. |
| Dice are not allowed here | `dice` was used in a filter. | Roll with a Roll binding in a formula instead. |
| side() is not available in a side's unit filter | `side()` was used inside a side's own unit filter. | Describe the side's units with `name()`, `marker()` or a property instead. |
| Unknown property 'x' | In a filter, a name is not a property in the pack. | Fix the name, or add the property. |
| x has no value on this piece | In a filter, the property does not apply to this unit, or its value is another side's hidden value. | Check with `has(x)` first, or check the property's scope and Applies to. |
| Raft A is not in a hex | `hex()` ran on a counter with no grid location (the message names the counter). | Run it on a counter on a gridded map. |
| hex() needs a unit / keyed() needs a unit | `hex()`, or `keyed()` of a counter property, ran with no counter. | Run the formula from a counter. |
| hex() is not available here / distance() is not available here / keyed() is not available here | The function ran where there is no module or game to ask. | Use it in a formula run in a module. |
| distance(): no hex named "0999" on any board | A name given to `distance()` is not a hex of any board. | Fix the name. |
| distance(): "0412" and "0507" are not on the same board | The two hexes are on different boards. | Pick two hexes of one board. |
| distance() needs a hex or square grid | The board has another kind of grid, or none. | Use a board with a hex or square grid. |
| distance() needs hex names as text | An argument was a number. | Put the name in quotes. |
| keyed() needs a property name / keyed() needs a holder name | The first argument (or the third) is not a bare name. | Write `keyed(FordDepth, hex())`. |
| FordDepth is not keyed | `keyed()` names a property with no Keyed by. | Set **Keyed by** on the property, or read it by its name. |
| keyed(): no row "Sloop" in table "Hull types" | The key is not a row of the property's table. | Use a row key of the table. |
| keyed(): the turn must be a whole number of at least 1 | A turn key that is not a whole number from 1 to 100000. | Fix the key. |
| keyed(): no hex named "9999" on any board | A hex key no board has. | Fix the name. |
| keyed(): the key is too long | A key over 64 characters. | Shorten it. |
| "FordDepth" is keyed; read it with keyed() | A binding or a bare name in a filter read a keyed property. | Read it with `keyed(FordDepth, key)`. |
| sum() cannot total keyed property "FordDepth" | A piece sum was given a keyed property. | Total another property. |
| keyed() reads data this player cannot see | A player's view lacks a value `keyed()` reads (see `keyed` above). | Nothing to fix: the host works it out. |
| The key for FordDepth: no hex named "9999" on any board | An output's Key is not a hex of a board (the same prefix for the other key messages). | Fix the output's **Key**. |

### Messages about the formula's setup

These come from the bindings and outputs rather than the formula text.

| Message | What it means | How to fix it |
|---|---|---|
| Unknown property: x | A Ledger tab filter names a property that is not in the rules pack. | Fix the name when you edit the tabs. |
| Waiting for input | An Input variable has no value yet. | Type one in the Formula dialog, or give the binding an automatic value. |
| Needs a unit | A Unit or Location variable, or a **Same hex** Piece or Draw binding, has no counter to read. | Run the formula on a counter. |
| Only known when a unit is moved | A Moved variable was used where nothing has moved. | Use the formula as an on-move step of a phase, or type a distance. |
| Choose one of the listed pieces | A Piece binding's pick is not on the list (the counter moved, was deleted or was hidden after the list was shown). | Pick again from the list. |
| No piece matches "Speed > 3" | A Piece binding's which-pieces filter matches no counter the player can see (with a blank filter, `No piece to choose`). | Widen the filter, or run the formula when a counter matches. |
| Upgraded Speed of Raft A reads data this player cannot see | A table read on a player's own view would use the piece's non-Public override (see "A piece's own value for a table rating"), and the piece is of a side that player does not hold. A Table binding is left without a value; an inline `lookup` stops the run. | Nothing to fix: the host (or a player holding every side) works it out. |
| Hidden from this player | A Piece or Draw binding reads a non-Public property of another side's counter on a player's own view, or a Piece output's target is another side's masked counter there. | Nothing to fix: the host (or a player holding every side) works it out. |
| No piece in this hex | A **Same hex** Piece binding found no other counter in the hex of the counter the formula runs on. | Run the formula when another counter is there, or untick **Same hex**. |
| An output refers to a deleted Piece variable | A Piece output names a variable that is no longer a Piece or Draw binding. | Pick the variable again in the output's **Piece** column. |
| Choose one of the listed holders | A Holder binding with Holder choices got a holder that is not on the player's list (its owner changed, or the label was wrong). | Pick again from the list. |
| No holder to choose | None of the listed holders is one the player may act on now. | Run the formula when the player's side holds one, or list another holder. |
| The draw count for Hits must be a whole number from 0 to 1000 | A Draw binding's **Draw count** gave a number that is negative, over 1000, not whole or not a number. | Fix the expression. |
| Boats of SC 3 Barge must be a whole number from 0 to 1000 to draw from | A counter in a Draw's pool has a weight outside 0 to 1000 or not whole. | Fix the counter's value or the filter. |
| An output writes the placed piece, but nothing was placed | A Placed output ran but the formula's Place output did not place a counter. | Give the formula one Place output. |
| Palette entry "Markers/Wreck" was not found in the module | A Place output names a palette entry the module does not have. | Pick a name from the **Palette entry** list. |
| Fort has no layer "Levels" | A Set layer output, or `layer()`, names a layer the counter lacks. | Check the layer's name in the module (the Set layer output's **Layer** column). |
| Layer "Levels" has no level 4 | A Set layer value is outside 0 to the layer's number of levels (0 only when the layer can be switched off). | Fix the output's **Value**. |
| Layer "Levels" follows a property, so it cannot be set | A Set layer output names a layer whose level comes from a property. | Write the property instead. |
| Layer "Levels" follows a property; read the property instead | `layer()` names such a layer. | Read the property. |
| Minefield has no mask | A Set mask output ran on a counter without a Mask trait. | Use a counter that has one. |
| The mask value must be 0 or 1 | A Set mask output's value is something else. | Use 1 to mask, 0 to unmask. |
| Map "Dead pile" was not found in the module / Map "Dead pile" has no board | A Remove output's **To map** names a map that is missing or has no board. | Pick a map from the **To map** list that has a board. |
| layer() needs a layer name in quotes | `layer` was given something other than quoted text. | Write `layer("Levels")`. |
| layer() is not available here | `layer()` was used where no counter is being read. | Use it in a formula run on a counter, or in a filter. |
| layer() cannot be read in this view | A player's view shows another side's masked counter, which has no readable layer. | Nothing to fix: the host works it out. |
| Not one of the choices for this piece | A typed value for an Input with a **Choices table** is not one of the keys the counter's list offers. | Pick from the list. |
| No choice for this piece | The Input's choices filter leaves no row for this counter. | Widen the filter, or run the formula on another counter. |
| Choices filter: {message} | The filter failed while it was tried (the message says why). | Fix the filter in the Workbench. |
| The choices table for Cargo was deleted | The Input names a table that no longer exists, or whose row keys are ranges. | Pick another table. |
| row() can only be used in a choices filter | `row()` was used in a formula, condition or other expression. | Use it only in an Input's **Choices filter**. |
| Variable x depends on itself | Variables refer to each other in a circle. | Break the circle. |
| variable "x" has no binding | The Workbench: the formula uses a name that has no row under Bindings. | Add the binding. |
| variable "x" is bound but not used in the formula | A warning: a binding is not used. | Remove the binding or use it. |
| refers to unknown table "x" | The Workbench: `lookup` in a filter or condition names a table that is not in the pack, or a Table binding's table was deleted. | Fix the name, or pick a table for the binding. |
| refers to unknown property "x" | The Workbench: an applies-to, filter or condition names a property that is not in the pack. | Fix the name. |
| refers to unknown holder "H" | The Workbench: `holder(H, ...)` names a holder that is not in the pack. | Fix the name, or add the holder. |
| refers to "P", which is not a holder property | The Workbench: `holder(..., P)` names a property whose scope is Pieces. | Use a property whose scope is Holders or Both. |
| sum() names unknown property "P" | The Workbench: `sum`, `maxof`, `minof` or a `hex` function names a property that is not in the pack (the message names the function). | Fix the name, or add the property. |
| sum() needs a piece property, not "P" | The Workbench: the property's scope is Holders, which pieces do not have. | Use a property whose scope is Pieces or Both. |
| the filter of sum() does not parse: ... | The Workbench: the quoted filter is not a valid filter (the message says why). | Fix the filter; write quotes inside it as `\"`. |
| hexsum() is not available in a side's unit filter | The Workbench: a side's unit filter, or a property's applies-to, uses a `hex` or `attached` function; those are worked out without the map grid. | Describe the units another way. |
| shows once (its condition never reads the unit), so {unit} is left out | A note: a game-level warning or alert names `{unit}` in its message. | Remove `{unit}`, or add a unit read such as `and name() != ""` to list each unit. |
| Side "North": unit filter may not use side() | The Workbench: a side's unit filter uses `side()`. | Remove it. |
| The result is not a number, so it cannot change Fuel | An output writes into a property, but the formula gave text or true/false. | Make the formula give a number. |
| The value for Ammo is not a number | An output's **Value** gave text or true/false. | Make the expression give a number. |
| Medicine is not one of the output's properties | **Pick by** gave a number outside the list or a name that is not in **Choose from**. | Check the expression against the list. |
| output 1 has a property list but no "Pick by" | The Workbench: **Choose from** is filled in but **Pick by** is empty. | Fill in **Pick by**, or clear **Choose from**. |
| Property "Range": the expression does not parse: ... | The Workbench: a property's Expression is not a valid expression (the message says why). | Fix the Expression. |
| Property "Range" is computed from itself: Range → Reach → Range | The Workbench: computed properties read each other in a circle. | Break the circle. |
| Formula "Refuel": output 1 writes computed property "Range" | The Workbench: an output writes a computed property, whose value is never stored. A run of such a formula is refused with the same text and writes nothing. | Write the stored property it is computed from. |
| Property "Range" is computed; its Default and Sheet field are ignored | A warning: a computed property also has a Default or a SheetField. | Clear them, or clear the Expression. |
| Side "Red": unit filter may not read Private property "Morale" while the pack has Private computed properties (players' views could not tell whose pieces they hide) | A side's unit filter reads a Private property, and some computed property counts as Private: in a player's view that filter could not tell whose pieces are whose. | Make the property the filter reads Public, or define the side with Public values (name, marker). |
| Side "Red": unit filter may not look up table "Rafts", which a Private property overrides | A side's unit filter calls `lookup` of a table that a non-Public property overrides (see "A piece's own value for a table rating"): in a player's view that lookup is hidden data, so the filter could not tell whose pieces are whose. | Look up a table no Private property overrides, make the overriding property Public, or define the side with Public values (name, marker). |
| Property "Range" is Public but is computed from hidden data; players who do not hold every side see it as Private | A warning: see "Computed properties" above. | Set it to Private, or compute it from Public values only. |
| Property "Upgraded Speed" overrides a table that no longer exists | The Workbench: the property's Overrides table was deleted. | Pick another table, or clear Overrides table. |
| Property "Upgraded Speed" overrides column "Speed", which table "Rafts" does not have | The Workbench: the Column is not a column of the table (it was renamed or removed). | Pick a column of the table. |
| Property "Upgraded Speed" overrides a table but is not a piece property | The Workbench: a property of scope Holders cannot override a cell; only a piece has a row of its own. | Set its scope to Pieces or Both, or clear Overrides table. |
| Properties "Upgraded Speed" and "Refit Speed" both override Rafts, column Speed; "Upgraded Speed" wins | A warning: two properties override the same column (one dimension: `both override Costs; "..." wins`). For a piece that has both, the first in the pack's order is used. | Remove one, or accept the order. |
| Range is computed (= Fuel * 2 + Readiness) and cannot be edited | The Ledger: a computed value was typed over. | Change the values it is computed from. |
