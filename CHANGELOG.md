# Forge and Feast 2.5.0

Fixes from the first in-game test

  - Placement: the range, kiln and retort now face towards you when placed
    (they were facing away). The wrench still rotates them.
  - Doors: the doors now open when you open the stove window and close when you
    close it. The game was sending a different "window opened" message than the
    one the mod was listening for, so the doors never moved.
  - Sounds: fixed the "Audio File not found" warnings (ceramicplace and anvil
    sounds now come from the base game properly). The door sound is fixed too.
  - Windows: Kitchen Range and Kiln Oven windows are re-laid out with space
    between the fuel slot and the rows, section headings (Burner plates, Oven,
    Firing slots, Crucible ingredients) and wider gaps between rows.
  - "Heating up..." no longer shows when the stove is cold. It now says
    "Needs fuel and heat".
  - Frying: a pot holding a single food item (raw meat, for example) now cooks
    it, like a frying pan. The cooked food stays in the pot slot. Single items
    can also still go straight onto a burner plate. No separate frying pan item
    was added.
  - Open range oven: the inside of the oven is now dark and sooty instead of the
    pink riveted metal.
  - Pipes: a pipe running sideways under a roof now sits tight against the roof.
    Stovepipes now count as solid for the room-sealing check, so a pipe going
    through the roof no longer opens the room. A pipe that ends under a roof of
    one or two solid blocks with a chimney above it now counts as connected to
    that chimney, so you don't have to cut the roof open.
  - Smoke: the little wisp from the stove's own rear stub now only shows when
    no pipe is fitted on top of it (it used to keep smoking beside a connected
    pipe).
