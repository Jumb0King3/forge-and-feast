# Forge and Feast 2.4.0

Crucible smelting in the Kiln Oven (Iron and Steel only)

  - Iron and Steel Kiln Ovens can smelt metal. Put a crucible in any firing
    slot (no separate crucible slots).
  - Crucibles and clay pots/moulds work together: mixed loads smelt and fire
    at the same time, each in its own firing slot.
  - Each firing slot has four ingredient slots beside it. They only accept
    items while a crucible is in that slot. Removing the crucible drops its
    ingredients.
  - Uses the game's own crucible rules: the highest melting point in the mix
    must be reached, then it runs for the combined melt time.
  - Status text per slot: Needs X C, Smelting N%, Add ingredients, Not a valid
    mix, Molten metal - pour into a mould.
  - The finished metal replaces the crucible in the same slot. Take it out and
    pour it into a mould as you would at a firepit.
  - Iron and Steel kiln window is one column of rows. Copper and Bronze keep
    the two-column layout. Copper and Bronze kilns cannot smelt (they only
    reach 1000 and 1100 C).
