> **Still in testing. Use at your own risk, and please [report any problems](../../issues/new/choose) you run into.**

# 🔥 Forge and Feast

*Cook, fire, smelt and smoke indoors with six stoves in four tiers of iron-age kitchen gear: **Copper, Bronze, Iron and Steel**. Every tier is a solid step up from the base game without skipping the progression.*

## At a glance

|  | Copper | Bronze | Iron | Steel |
|---|---|---|---|---|
| **Range burner plates** | 1 | 2 | 3 | 4 |
| **Range oven slots** | – | 1 | 2 | 3 |
| **Range cooking speed** | ×1.1 | ×1.25 | ×1.4 | ×1.6 |
| **Kiln slots** | 2 | 3 | 4 | 5 |
| **Kiln firing time** | 12 h | 10 h | 8 h | 6 h |
| **Kiln firing heat** | 1100°C | 1475°C | 1700°C | 1950°C |
| **Crucible Furnace max heat** | 1100°C | 1475°C | 1700°C | 1950°C |
| **Crucible capacity** | ×1 | ×1.5 | ×2 | ×2.5 |
| **Potbelly cooking speed** | ×1.0 | ×1.1 | ×1.25 | ×1.4 |
| **Potbelly fuel lasts** | ×1.5 | ×1.75 | ×2.0 | ×2.25 |
| **Potbelly max heat** | 450°C | 500°C | 550°C | 600°C |
| **Smoker hooks** | 4 | 6 | 8 | 10 |
| **Smoking time** | 24 h | 20 h | 16 h | 12 h |
| **Large Pot servings** | 8 | 10 | 12 | – |
| **Extra fuel stacks** | – | 1 | 2 | 3 |

Times are in-game hours. Kiln times are for a pot or mould; charcoal or coal roughly halves them.

---

### 🍲 Kitchen Range

*One fire heats several burner plates and an oven.*

- **1 to 4 burner plates.** Each takes a cooking pot, or a single item like a steak.
- **Built-in oven** from Bronze up. Bread comes out when it's done and never chars.
- **Food goes where it would in real life.** Baked and roasted food (bread, dough, pies, pemmican, roasted nuts) goes in the oven. Fried and boiled food (nuggets, sausages, dumplings, grain) goes on the burners. Raw meat, fish and vegetables can go in either.
- **Burners cook like a pan:** food moves to the output slot when it's done, before it can char.
- **Faster than a firepit,** from ×1.1 on Copper up to ×1.6 on Steel.
- **See what's cooking:** stew shows in the pot and meat sits on the plate.
- **Large Pots** cook bigger batches than the base game's 6-serving clay pot.

### 🏺 Kiln Oven

*A bottle kiln for firing pottery and moulds.*

- **Fires moulds, pots and crucibles,** quicker than a pit kiln, on the in-game clock.
- **A stepped firebrick dome** bound with metal hoops, with a brick flue stack at the back.
- **Uses much less fuel per firing** than an open fire.
- **Melting metal has moved** to the Crucible Furnace.

### 🔥 Crucible Furnace

*A tilting furnace that melts metal and pours it for you.*

- **Put a fired crucible in the furnace** and its ingredients beside it, like a firepit. Each tier runs hotter than its fuel: Copper melts copper and every bronze, and Steel reaches 1950°C.
- **Higher tiers melt faster and hold more,** up to 2.5 times as much on Steel.
- **Pour by hand:** set a mould in front of the spout and hold right-click on the crank. The drum tips forward and pours straight into the mould.
- **Pour by mechanical power:** run an axle into the gearbox and flip the clutch lever. It tips and pours on its own whenever the metal is molten and there's an empty mould in front, then waits for the next one.
- **Pours into anything you can pour into:** tool moulds, both halves of an ingot mould, and other mods' moulds.
- **Big moulds:** while it burns, a part-filled mould in front of it is kept hot, so an anvil mould can be filled over two or three crucibles.
- **Optional slag** (server setting `CrucibleFurnaceSlag`): skim it off with tongs or the pour comes out short.
- **Takes a stovepipe** on its flue, like the other stoves.

### ⚫ Charcoal Retort

*Wood in, charcoal out, with no pit to dig.*

- **Firewood and logs → charcoal,** at twice the yield of a base game charcoal pit.
- **Black and brown coal → coke,** at the base game coke oven rates, on every tier.
- **Runs on the in-game clock,** at about 1 in-game hour per charcoal.
- **Open the hatch** to see the wood stacked inside or the charcoal ready.
- **Very smoky,** so vent it well.

### 🫖 Potbelly Stove

*A small cast-iron stove for cabins and corners.*

- **Works like a firepit:** cooking pots, single items, serving meals from the pot, shift-adding fuel and lighting it all work the same. Other mods that cook on a firepit work on it too.
- **One cooking spot** on the hot plate on top, and the food or pot shows on it.
- **Fuel lasts longer** in its closed box: ×1.5 on Copper up to ×2.25 on Steel.
- **Keeps a room warm** like a firepit, with the fire's crackle. Its doors swing open while you use it.
- **Takes a stovepipe** on the flue at the back.

### 🥓 Smoker

*A two-block farm smokehouse that smokes meat and fish.*

- **Hang raw meat or fish on the hooks** (4 to 10 by tier) and keep a small fire going. Smoking takes 24 in-game hours on Copper down to 12 on Steel, and pauses (doesn't reset) if the fire goes out. Skipped time (sleeping, `/time add`) counts while the fuel lasts.
- **Smoked red meat, bush meat, poultry and fish fillets** keep about a year. Cured meat smokes into **cured and smoked** meat, which keeps about 25% longer again. Both go in stews and soups like cured meat.
- **It smokes, it doesn't cure.** Cure meat with salt in a barrel, as in the base game.
- **Open the door** to see the meat hanging on its hooks. The firebox hatch opens too.
- **Wood gives full smoke;** charcoal and coal burn clean and smoke at a quarter of the speed.
- **Very smoky:** give it a chimney or a stovepipe on its roof, or put it outdoors.

### 🪵 Fuel Store

- **Bronze, Iron and Steel stoves hold 1, 2 or 3 extra stacks** of fuel behind the fuel slot, and top the fuel slot up as it burns.
- **Shift + right-click a stove with fuel** to put the whole stack in, or click the spare slots in its window with fuel. Click one with an empty hand to take a stack back out.
- **One kind of fuel at a time;** breaking the stove drops it.

### 💨 Smoke, Chimneys and Stovepipes

- **No chimney? The room fills with smoke.** Smoke rises and builds down from the ceiling; once it reaches your head you start choking, so crouching buys time. This also applies to the base game firepit and clay oven.
- **Some blocks let smoke out:** thatch, hay, reeds, leaves, wattle and doors, so a thatched hut with one small fire stays hazy instead of choking.
- **Everything that burns smokes:** torches, lamps and candles a little; forges, bloomeries, pit kilns and open fire a lot. A forge with a chimney above it doesn't smoke the room.
- **Smoke by fuel:** charcoal and coke burn clean, wood is normal, peat, dung and hay are very smoky.
- **Chimney draft:** a Clay Brick Chimney above a stove, or joined to it by stovepipe, makes it work 25% faster (up to 35% in the wind, less in the rain). Each chimney carries about one and a half fires' smoke, more if you stack it taller; too many fires on one chimney overload it.
- **Stovepipes join up on their own** to every stove, with bends, tees, crossings and corners (one block up or down and over, or round the corner of a wall).
- **Runs stay level:** a sideways run tucks up under a roof or ceiling, lies on a floor, or sits in the middle, and the whole run keeps the same height.
- **Through the roof:** a pipe ending under a roof goes up through it (up to 8 blocks thick, including VS Roofing roofs) into a brick chimney top of its own. Put a real chimney on the roof above it instead and the pipe uses that, sitting it down into the roof on a brick seat.
- **Through a wall:** hold a wrench in your offhand and a block in your main hand, and use it on the pipe to cover it. Chiselled blocks keep their carved shape, so a pipe can show through a carved hole.

### 🧩 Works with other mods

- **VS Roofing:** stovepipes pass up through its roofs and framing to a chimney or a chimney top of their own.
- **Real Smoke:** every stove makes Real Smoke smoke, and stovepipes carry it to the chimney. Forge and Feast's own room smoke switches off.
- **Butchering:** the Smoker makes Butchering's smoked meats, the same as its smoking racks.
- **Primitive Survival:** the Smoker makes its smoked meat, takes trussed meat (4 pieces each), fish fillets and jerky, and smokes crab and snake meat. Cure crab and snake meat with salt in a barrel. Its own smoker is replaced by the Forge and Feast Smoker.
- **Expanded Foods:** the Range puts each food in the oven or on a burner as it should be.
- **Rustic Cookery:** the Smoker makes its smoked slabs, smoked meat and smoked fish. Braising, roux and eggs work on the Potbelly Stove with tongs, and the base game clay oven can be made again for Pot Roasts.
- **Feverstone Wilds:** the Smoker makes smoked bison leg, ribs and cockatrice tail. They can also be roasted in the Range oven.
- **xSkills:** the Range, Potbelly Stove and Crucible Furnace give cooking and smelting XP and perks.
- **Hearth & Draught:** its flues count as chimneys.
- **Bricklayers:** stoves can be made with glazed bricks.
- **Carry On:** stoves can be picked up, if its settings allow it.
- **Also checked and working:** Metal Cooking Pots, Metal Cookware, All Classes and Art of Cooking pots, A Culinary Artillery, Better Firepit, Chimney Flues, Clinker Brick Chimney, Craftable lit chimneys and Simple Smokey Chimneys.

### ⚙️ Other Details

- Stoves **face you** when placed and can be turned with the **wrench**.
- Doors **open** when you use a stove and **close** when you leave.
- The base game **clay oven can't be crafted** while this mod is installed (unless Rustic Cookery is), because the Kitchen Range replaces it. The firepit is unchanged.

### 🔨 Crafting

Start with Copper, then upgrade each stove by putting a plate of the next metal on each side of it.

<p align="center"><b>Kitchen Range</b><br><img src="images/forgeandfeast_craft_kitchenrange.gif" alt="Kitchen Range recipes" width="600"></p>

<p align="center"><b>Kiln Oven</b><br><img src="images/forgeandfeast_craft_kilnoven.gif" alt="Kiln Oven recipes" width="600"></p>

<p align="center"><b>Crucible Furnace</b><br><img src="images/forgeandfeast_craft_cruciblefurnace.gif" alt="Crucible Furnace recipes" width="600"></p>

<p align="center"><b>Charcoal Retort</b><br><img src="images/forgeandfeast_craft_retort.gif" alt="Charcoal Retort recipes" width="600"></p>

<p align="center"><b>Potbelly Stove</b><br><img src="images/forgeandfeast_craft_potbelly.gif" alt="Potbelly Stove recipes" width="600"></p>

<p align="center"><b>Smoker</b><br><img src="images/forgeandfeast_craft_smoker.gif" alt="Smoker recipes" width="600"></p>

<p align="center"><b>Large Pots and Stovepipes</b><br><img src="images/forgeandfeast_craft_largepots_stovepipes.gif" alt="Large Pots and Stovepipes recipes" width="600"></p>

- **Copper tier:** fired bricks plus copper. The Kiln Oven takes 1 copper plate, the Retort 2, the Range and Potbelly Stove 1 plate and 1 ingot. The Smoker takes 5 planks, 2 stones and 1 copper plate.
- **Crucible Furnace:** 2 copper plates, a copper ingot and 6 fired bricks.
- **Higher tiers:** 2 plates of the new metal, one on each side of the previous tier.
- **Large Pots:** 2 ingots and 1 plate of the same metal. The Bronze pot takes any bronze.
- **Stovepipes:** 2 metal plates of any kind make 8 pipes.

---

📦 **Install:** needed on **both the server and every player's game**. Made for Vintage Story 1.22.

## Download

- ModDB (1-click install): https://mods.vintagestory.at/forgeandfeast
- Or take a zip from the [Releases](../../releases) page and put it, unzipped, in your Mods folder.

## Report a problem

Open an issue using the bug report form: [New issue](../../issues/new/choose). Please include your game version, the mod version and your log.

**Where to find your log**

The logs are in the Vintage Story data folder, in a folder called `Logs`:

| System | Data folder |
|---|---|
| Windows | `%appdata%\VintagestoryData` (paste that into the File Explorer address bar) |
| Linux | `~/.config/VintagestoryData` |
| Mac | `~/Library/Application Support/VintagestoryData` |

- **Playing on a server:** send `client-main.log` from your own game. If you run the server, also send `server-main.log` from the server's data folder.
- **Singleplayer:** `client-main.log` and `server-main.log` are both in the data folder above.
- Attach the file to the issue (drag it into the box), or paste just the lines with errors. Reproduce the problem first, then grab the log straight afterwards, so the error is near the end.

## Changelog

Release notes are on each release. The full history is in `CHANGELOG.txt`, and each commit is one released version.
