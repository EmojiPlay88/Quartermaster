# Formula Reference

Formulas are the small sums you write in the Rules Workbench: how much fuel a unit draws, whether a unit shows up in a filter, when an alert fires. This page lists everything the formula language can do. To see formulas in use, follow [Making a Rules Pack](Making-a-Rules-Pack). To run one, see the [Formula dialog](Window-Formula-Dialog).

The examples use the invented Riverland demo game: units have the properties Fuel, Ammo and Readiness, depots keep Stores, and the global holder keeps Weather. After most examples the page says what the formula gives.

## The basics

- **Three kinds of value.** A **number** (`3`, `0.5`), **text** in double quotes (`"Unit"`), and **true/false**. Numbers show with at most two decimals.
- **Literals.** Write numbers with digits and an optional decimal point (`2`, `.5`, `1.25`). There is no minus sign inside a number: `-3` is the minus operator applied to 3. Write text between double quotes. To put a quote inside text, write `\"`, and for a backslash write `\\`. Nothing else may follow a backslash. `true` and `false` are the two true/false values.
- **Names.** Anything else you type is a variable name: letters, digits and underscores, starting with a letter or underscore (`Fuel`, `Hexes`, `Supply_Left`). A name cannot contain a space. Where the name gets its value depends on where the formula is used; see [Where names get their values](#where-names-get-their-values).
- **Capital letters.** Function names, `and`, `or`, `not`, `true` and `false` may be written in any case: `MIN(1, 2)` and `min(1, 2)` are the same. Variable names must match the name you gave them exactly. Comparing text with `==` and `!=` ignores capitals (`"Unit" == "unit"` is true).
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

If Fuel is 3, this gives 17: `10 * 2` first, then the minus sign on Fuel, then the sum.

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

### dice(count, sides)

Rolls `count` dice with `sides` sides each, and gives their total. `count` is a whole number from 1 to 100 and `sides` a whole number from 2 to 1000. The roll goes into the shared dice log. Dice work only where a game can roll them (for example in a formula run from the Formula dialog); they are not allowed in filters.

```formula
dice(2, 6)
```

Gives a number from 2 to 12. For a roll the player sees as a named variable, bind a variable of Kind **Roll** instead (see [Where names get their values](#where-names-get-their-values)).

### has(Property)

True when the unit has a value for that property. Use it to keep a formula or filter to the units it makes sense for. Give the property's name bare, or in quotes if you prefer.

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

The name of the counter the formula is about, as text. Takes no arguments. It needs a unit; used with no unit it is an error.

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

Every name used in the formula needs a binding, and every binding should be used; the Workbench's Problems list tells you when they do not match. The longer explanation, with pictures of each case, is in [Making a Rules Pack](Making-a-Rules-Pack#step-4-formulas). How the answers are shown and applied is in the [Formula dialog](Window-Formula-Dialog).

**In a filter** (a formula's Applies to, a property's AppliesTo, a side's unit filter, an alert's condition, or a Ledger tab's filter), there are no bindings. A bare name means the **property of that name on the unit being tested**. If the unit has no value for it, that is an error, so guard with `has(...)`. The functions `name()` and `marker("...")` read the unit too. `lookup` works in filters; `dice` does not.

```formula
has(Readiness) and Readiness >= 3
```

True for units that have Readiness 3 or more.

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
| No table named 'x' | A `lookup` names a table that does not exist. | Check the spelling against the Tables tab. Table names must match exactly; use one word. |
| No row for 5 in SupplyByDistance | No row key in the table matches the value (the message shows the value and the table). | Add a row, or an open-ended row such as `5+`. |
| No column for x in Table | The same, for the column of a two-dimension table. | Add a column that matches. |
| Table T needs a column | `lookup` was given only a row, but the table has two dimensions. | Give a column as the third argument. |
| This formula needs a unit | `name()` or `marker(...)` was used in a formula that was run with no counter. | Run the formula from a counter, or choose a Test piece in the Preview. |
| No marker 'x' | The counter has no marker with that key. | Check the key. Riverland's keys are Side, Kind and Type. |
| Dice are not allowed here | `dice` was used in a filter. | Roll with a Roll binding in a formula instead. |
| Unknown property 'x' | In a filter, a name is not a property in the pack. | Fix the name, or add the property. |
| x has no value on this piece | In a filter, the unit does not have that property. | Check with `has(x)` first. |

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
| refers to unknown property "x" | The Workbench: an applies-to, filter or condition names a property that is not in the pack. | Fix the name. |
| The result is not a number, so it cannot change Fuel | An output writes into a property, but the formula gave text or true/false. | Make the formula give a number. |
