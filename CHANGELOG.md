# Forge and Feast 2.7.0

Food shows while cooking, door sounds, open oven, pipe bends

  - Door sounds: the coke-oven door sound now plays when doors open and close.
    The place sounds for the stoves and pipes are fixed too (they were pointing
    at the wrong asset folder in 2.5.x and 2.6.x).
  - Open oven: removed the red glow panel that sat inside the open Kitchen
    Range oven (it belonged to the closed door's window). The cold open Kiln no
    longer shows a glowing back wall.
  - Cooking shows on the stove, like the firepit:
      - A single item on a burner plate (a steak, for example) now sits on the
        plate while it cooks, and stays there cooked until you take it out.
      - A pot shows its food: once the ingredients match a recipe you see the
        stew or meal in the pot, and a finished pot shows the cooked meal.
      - A pot with ingredients that don't make a recipe yet (or one piece of
        meat being fried) shows the pieces sitting in the pot.
  - Oven, kiln and retort items now use the right textures when drawn (items
    were being drawn without their proper textures before).
  - Stovepipes: a pipe that runs along the ceiling and then turns down the wall
    now lifts its bend too, so the run and the drop line up instead of leaving a
    step at the corner.
