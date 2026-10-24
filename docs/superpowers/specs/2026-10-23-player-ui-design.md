# Player-facing circuit UI

Turn the current closed-circuit player's development shell into a coherent family racing front end. Keep the existing Unity UI Toolkit, command handlers, profile persistence, bilingual text, course content and racing simulation.

Use the existing optional AssetTrialHud scene component to select the player presentation. Baseline diagnostic scenes retain their existing sandbox; the circuit player gets racer selection, a home menu, consistent setup screens and a compact race HUD. This is a presentation pass, not release qualification.

Approaches considered: changing only colors leaves the sandbox as the main menu; rewriting navigation and simulation contracts adds unnecessary risk. Chosen implementation: player screen builders and a shared theme layered over the established command-driven shell.

Visual direction: ink/navy background, cream text, warm orange primary actions, mint highlights, actual in-game racer portraits and course previews. Clear headings, generous card spacing, visible selected states and keyboard focus. English and Hebrew wrap within tablet and PC widths. Touch controls remain on mobile; desktop gets keyboard hints in pause and a small pause affordance.

New and existing profile selections lead to the home menu. Quick Race is the primary action; Campaign, Time Trial, Garage, Challenges, Practice, local multiplayer and Settings remain reachable. Missing content stays unavailable. Save failures and recovery warnings remain visible.

Race HUD prioritizes position, lap, elapsed time, speed, boost and held gadget. Checkpoint debugging and persistent successful-save messages are omitted from normal racing. Pause/results use bounded solid panels over a dim scrim and sit above every HUD element.

Ship a separate ordinary Windows circuit build alongside a development build for reproducible screenshot/command checks. Preserve the user's running player and unrelated edits.
