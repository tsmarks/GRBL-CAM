# GRBL Cam

GRBL Cam is a WPF desktop foundation for a GRBL-focused CAM workflow. The current slice is intentionally centered on 3-axis milling so we can validate project structure, machine and tool data, job setup, operation editing, stock simulation, and GRBL post output before layering in heavier geometry and kinematics work.

## Current foundation

- Machine profile library for mill and future lathe configurations
- Kinematics model that already carries `3-axis`, `4-axis indexed`, `3+1`, `3+2`, `5-axis`, and `mill-turn` modes
- Tool library with geometry fields for square, ball, bull, lollipop, taper, V-point, drill, and related tools
- Job setup model for part source, stock source, alignment, work offsets, and indexed A/B/C orientation
- Multi-operation workflow with facing, pocket, profile, and drill operations
- GRBL-oriented post processor with manual tool-change pauses
- Approximate stock removal simulation to support early workflow testing and REST-planning groundwork
- JSON persistence for machine, tool, and job data

## What is intentionally stubbed for the next phase

- STEP B-rep import and face/edge/feature picking
- Solid-model-aware gouge checking
- True REST toolpath generation from remaining material
- 4th/5th-axis kinematic transforms and collision handling
- Lathe and mill-turn cycle support
- Rich verification views, stock staging, and setup sheets

## Suggested next milestones

1. Add a geometry import layer for STEP solids and expose a selectable face/edge graph to the UI.
2. Replace manual feature envelopes with model-backed feature references while keeping envelope fallback for rapid testing.
3. Upgrade the stock simulation from a height-field preview to operation-aware material state that can drive REST machining.
4. Split the planner into strategy modules per operation family so indexed and simultaneous axis modes can plug in without rewriting the desktop shell.
5. Add a project/export format for machine libraries, tool libraries, and reusable machining templates.
