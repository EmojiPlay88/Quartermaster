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

## What a phase condition may read

A phase can have a **Runs only if** condition (see [Making a Rules Pack](Making-a-Rules-Pack#phase-conditions)). A condition is checked when Quartermaster is about to start the phase, with no unit in hand, so it may read only:

- global properties (a global property that has not been set reads its Default);
- tables, through `lookup`;
- `turn()` and `stage()` of the position being tested;
- `phaseside()`, the name of the side whose phase is being tested;
- constants and the pure functions (`if`, `min`, `max`, `round`, `floor`, `ceil`, `abs`, `clamp`, `mod`, `startswith`, `contains`).

It may not use `name()`, `marker()`, `has()`, `side()`, `map()`, `holder()`, `dice()` or the values of a piece or a side's holder. A formula that fails never skips the phase: the phase runs and a notice tells you what went wrong.

## Where names get their values

A bare name in a formula (`Fuel`, `Hexes`, `Weather`) is a **variable**. How it gets its value depends on where the formula lives.

**In a formula on the Formulas tab**, each variable has a row under **Bindings** that says where its value comes from. The kinds are:

| Kind | Where the value comes from | Example |
|---|---|---|
| Unit | a property of the counter the formula is run on | Fuel |
| Global | a property kept on the global holder | Weather |
| Holder | a property of a named holder, such as a depot | Stores of North Depot |
| Table | a cell of a table, chosen by a row (and column) expression | Gain, row Hexes |
| Location | the name of the hex or zone the counter stands on (text) | 0302 |
| Input | a value the player types in the Formula dialog, with limits, choices and an automatic value for hands-off play | Hexes |
| Roll | a dice roll such as 1d6 | Die |
| Moved | the distance the counter just moved, in hexes (known only when it is moved) | Moved |

Every name used in the formula needs a binding, and every binding should be used. A name that appears in another binding's automatic value (for example `Hexes` in `auto: Hexes + 1`) counts as used when that other binding is itself used, but a binding's own name does not count as using itself; the Workbench's Problems list tells you when they do not match. The longer explanation, with pictures of each case, is in [Making a Rules Pack](Making-a-Rules-Pack#step-4-formulas). How the answers are shown and applied is in the [Formula dialog](Window-Formula-Dialog).

**In a filter** (a formula's Applies to, a property's AppliesTo, a side's unit filter, an alert's condition, or a Ledger tab's filter), there are no bindings. A bare name means the **property of that name on the unit being tested**. If the unit has no value for it yet, the property's **default** is used, as long as the property applies to that unit (its scope includes units, and its own Applies to, if it has one, matches). `has(Readiness)` still means "this unit has a value in the ledger", so it stays false until a value is set. If the property does not apply to the unit, that is an error, so guard with `has(...)`. In a player's view of the game (hot-seat view or an online guest), a value may be hidden rather than unset, so private values, and every value of another side's masked unit, do not fall back to their default there. The functions `name()`, `marker("...")`, `side()` and `map()` read the unit too. `lookup` and `holder()` work in filters; `dice` does not.

**Bare names for global values.** In a filter, a bare name of a property whose scope is **Holders** reads the **game-level** (global) value, not a unit's. Names of Pieces or Both properties still read the unit. This is how a filter such as `Weather == 2` works: it is true or false for the whole game. In a rare case this changes an old pack: a property moved from scope Both to Holders that still has values on pieces now reads the global value in a filter, not the piece's.

**A filter that never reads a unit shows once.** A phase warning or an alert whose condition reads no Pieces or Both property and uses none of `name()`, `marker()`, `has()`, `side()` or `map()` is a **game-level** rule. It is checked once, not once per unit. A game-level warning shows as one line in the phase panel with no units under it: its text is the message, or the condition text if the message is empty. A game-level alert has one match, on the global holder: its text is the message, or the alert's Name if the message is empty. A game-level decision reads just its text, with no "— 0 units". Text left over at the start of the message when `{unit}` is removed, such as the ": " of "{unit}: set up", is dropped. The Workbench notes a game-level warning or alert whose message uses `{unit}`, since that name is left out.

This changes some older packs. A warning or alert whose condition reads only constants, `turn()`, `stage()`, `mod`, or `lookup` on those used to list every unit, one row each, and now shows one line. A game-level alert with a Side no longer applies that side's unit filter. To get one row per unit back, add a unit read to the condition, for example `and name() != ""`.

```formula
has(Readiness) and Readiness >= 3
```

True for units that have Readiness 3 or more.

## Outputs

A formula's **Outputs** say what its result changes. Two optional columns in the outputs grid change what each output does.

**A value per output.** Normally every output gets the formula's result. Type an expression in **Value** and that output gets its own value instead, worked out in the same run (the same dice, inputs and bindings as the text). The formula's result is not a variable there. A blank **Value** means the formula's result. The working line shows each output's own value, such as `Fuel 140 - 12 = 128; Ammo 30 - 6 = 24`. If the value is not a number the run stops with `The value for Fuel is not a number`.

An invented example: a convoy loses supplies to one roll of `Loss` (a Roll of `1d6`). The text is `Loss`, the Fuel output keeps a blank Value, the Ammo output has Value `Loss * 2` and the Rations output has Value `max(Loss - 1, 0)`. One roll then takes from three supplies, and no scratch property is needed.

**A property picked at run time.** In **Choose from**, list properties separated by commas (for example `Fuel, Ammo, Rations`) and give **Pick by** an expression. A number picks the Nth property in the list (1 is the first); text picks the property with that name (capitals and spaces at the ends do not matter). Anything else stops the run with `Medicine is not one of the output's properties`. With a list but no **Pick by**, the Workbench reports `Formula "Raid": output 1 has a property list but no "Pick by"`. An empty list means the output's own **Property** is used. If you delete a property from the pack, it is removed from every list.

For example, an Input `Slot` of `1..3` and Pick by `Slot` lets the player choose which of Fuel, Ammo or Rations the output changes, with one formula instead of three. Values and the picked property are all worked out before anything is written, so a run that fails changes nothing.

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
| No table named 'x' | A `lookup` names a table that does not exist. | Check the spelling against the Tables tab. Write the name as it appears on the Tables tab (capitals don't matter); use one word. |
| No row for 5 in SupplyByDistance | No row key in the table matches the value (the message shows the value and the table). | Add a row, or an open-ended row such as `5+`. |
| No column of Landing row 2 holds 7 | `lookuprange` found the row, but no cell of it holds the value (the message shows the table, the row and the value). | Add a cell that holds it, such as an open-ended `6+`. |
| lookuprange needs a two-way table | `lookuprange` was given a table with one dimension. | Use a table with rows and columns, or use `lookup`. |
| count() needs a roll binding, not Fuel | Validator: `Formula "{name}": count() needs a roll binding, not {variable}`. The first argument of `count` is not a Roll binding. | Make that variable Kind Roll. |
| count() needs each die of Planes: type them separated by spaces | Physical dice: a single number was typed for a roll that `count` reads. | Type every die, such as `1 2 5 6`. |
| Planes needs 4 dice from 1 to 6 | Physical dice: the typed list has the wrong number of dice, or a die outside the range. | Type exactly that many dice, each within the range. |
| 'Planes' dice count must be a whole number from 1 to 100 | A Roll binding's **Dice count** expression gave something else. | Make the expression give a whole number from 1 to 100. |
| lookuprange() is not available here / count() is not available here | The function was used where it cannot run (`count` outside a formula run). | Move it into a formula with a Roll binding. |
| No column for x in Table | The same, for the column of a two-dimension table. | Add a column that matches. |
| Table T needs a column | `lookup` was given only a row, but the table has two dimensions. | Give a column as the third argument. |
| This formula needs a unit | `name()` or `marker(...)` was used in a formula that was run with no counter. | Run the formula from a counter, or choose a Test piece in the Preview. |
| No marker 'x' | The counter has no marker with that key. | Check the key. Riverland's keys are Side, Kind and Type. |
| Dice are not allowed here | `dice` was used in a filter. | Roll with a Roll binding in a formula instead. |
| side() is not available in a side's unit filter | `side()` was used inside a side's own unit filter. | Describe the side's units with `name()`, `marker()` or a property instead. |
| Unknown property 'x' | In a filter, a name is not a property in the pack. | Fix the name, or add the property. |
| x has no value on this piece | In a filter, the property does not apply to this unit, or its value is another side's hidden value. | Check with `has(x)` first, or check the property's scope and Applies to. |

### Messages about the formula's setup

These come from the bindings and outputs rather than the formula text.

| Message | What it means | How to fix it |
|---|---|---|
| Unknown property: x | A Ledger tab filter names a property that is not in the rules pack. | Fix the name when you edit the tabs. |
| Waiting for input | An Input variable has no value yet. | Type one in the Formula dialog, or give the binding an automatic value. |
| Needs a unit | A Unit or Location variable has no counter to read. | Run the formula on a counter. |
| Only known when a unit is moved | A Moved variable was used where nothing has moved. | Use the formula as an on-move step of a phase, or type a distance. |
| Variable x depends on itself | Variables refer to each other in a circle. | Break the circle. |
| variable "x" has no binding | The Workbench: the formula uses a name that has no row under Bindings. | Add the binding. |
| variable "x" is bound but not used in the formula | A warning: a binding is not used. | Remove the binding or use it. |
| refers to unknown table "x" | The Workbench: `lookup` in a filter or condition names a table that is not in the pack, or a Table binding's table was deleted. | Fix the name, or pick a table for the binding. |
| refers to unknown property "x" | The Workbench: an applies-to, filter or condition names a property that is not in the pack. | Fix the name. |
| refers to unknown holder "H" | The Workbench: `holder(H, ...)` names a holder that is not in the pack. | Fix the name, or add the holder. |
| refers to "P", which is not a holder property | The Workbench: `holder(..., P)` names a property whose scope is Pieces. | Use a property whose scope is Holders or Both. |
| shows once (its condition never reads the unit), so {unit} is left out | A note: a game-level warning or alert names `{unit}` in its message. | Remove `{unit}`, or add a unit read such as `and name() != ""` to list each unit. |
| Side "North": unit filter may not use side() | The Workbench: a side's unit filter uses `side()`. | Remove it. |
| The result is not a number, so it cannot change Fuel | An output writes into a property, but the formula gave text or true/false. | Make the formula give a number. |
| The value for Ammo is not a number | An output's **Value** gave text or true/false. | Make the expression give a number. |
| Medicine is not one of the output's properties | **Pick by** gave a number outside the list or a name that is not in **Choose from**. | Check the expression against the list. |
| output 1 has a property list but no "Pick by" | The Workbench: **Choose from** is filled in but **Pick by** is empty. | Fill in **Pick by**, or clear **Choose from**. |
