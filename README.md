# Synthos Poi Cross Editor Compare

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/GRIMREAPER35487/Poi-Cross-Editor-Compare?include_prereleases)](https://github.com/GRIMREAPER35487/Poi-Cross-Editor-Compare/releases)

Extends Poiyomi's **Cross Shader Editor** (Thry Editor) for VRChat avatar work: fill it with an avatar's materials in one click, open as many editors as you like, and compare two material sets side by side with every difference highlighted.

### Grab avatar materials

Two buttons under the material list:

- **Grab avatar (enabled):** replaces the list with the materials on the avatar's active, enabled renderers, skipping anything tagged `EditorOnly`. This is what actually ships.
- **Grab avatar (all):** replaces the list with the materials on every renderer on the avatar, including disabled and `EditorOnly` objects.

The avatar is the one containing your current selection. If nothing on an avatar is selected, it uses the only avatar in the scene, or asks you to pick when there are several. Prefab mode is supported.

### Drop zone

Next to the buttons there's a **drop zone**. Drag scene objects, prefabs or models from the Hierarchy or Project window onto it to *add* the materials of every renderer under them (disabled ones too) to the list.

### Several windows

**Poi → Thry → Cross Shader Editor** now opens another Cross Shader Editor window every time instead of focusing the existing one, so you can work on several material sets side by side. (If Unity's internal menu API isn't available, Thry's entry is left alone and a **New Cross Shader Editor** entry is added next to it instead.) Thry's own "Add to Cross Shader Editor" menu items still only send to one of the open windows.

### Compare two windows

With two or more Cross Shader Editor windows open, each gets a **Highlight differences** button. Clicking it pairs that window with the other one (or lets you pick, with three or more open). The other window is docked to the right of the one you clicked in and takes on its open sections. From then on, opening or closing a section in either window does the same in the other, invisible padding is added wherever one side has rows the other doesn't so every shared row sits at the same height, and the two scroll together. In both windows it also marks what differs between their enabled materials:

- **Orange:** a setting differs (slider, colour, toggle, dropdown).
- **Purple:** a texture differs, or its tiling / offset.
- **Blue** (on a section header): the section is switched on in one window and off in the other.
- **Pink:** the property is marked **Animated** (or Animated + Renamed) in one window but not the other. A row that only differs in that is tinted pink; a row that also differs in value keeps its colour and gets a pink bar on its right edge. Section headers get the pink bar when their on/off toggle is marked differently.
- **Thin grey bar and "N different"** (on a section header): the section contains differences, so you can see which collapsed sections to open.

The highlights update live as you edit, and **Stop highlighting** turns all of that off and removes the padding (the windows stay docked).

## Requirements

- **Unity 2022.3**
- **Poiyomi** (or anything else that ships Thry Editor). Without it the package does nothing.
- Only Thry-based shaders can be compared. VRChat's own shaders use Unity's standard material inspector, which the Cross Shader Editor doesn't show.

## Installation

Add the Synthos VPM listing to the VRChat Creator Companion, then add **Synthos Poi Cross Editor Compare** to your project.
