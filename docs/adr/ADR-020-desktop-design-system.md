# ADR-020: Desktop design system and operations-console layout

* Status: Accepted
* Date: 2026-10-10
* Phase: after 12 (UI/UX overhaul)

## Context

The desktop client grew phase by phase: a toolbar, a status bar and two tabs, with colours written straight into the
views (`#16212C`, `LimeGreen`, `Gold`, `OrangeRed`...) and every panel a rounded box. It worked, but:

* no shared vocabulary: each view picked its own sizes, colours and spacing; status colours were web named colours
  with uneven contrast;
* the right panel was one long scroll of identical boxes, so the important values (link, armed, mode, battery) were
  not visible at a glance, and the bottom status bar repeated them in plain text;
* the sign-in window was a 380 px stack of fields with no labels tied to inputs, a greyed-out button as the only
  feedback for empty fields, and no busy state;
* empty, loading and error states were mostly absent (an empty vehicle list was just empty);
* the arm/takeoff confirmation was built in code with its own colours.

The request was a full redesign towards "military command and control + premium industrial software": serious,
technical, readable for long sessions, without changing behaviour, data flow or security.

## Decision

1. **Tokens first.** `Themes/Tokens.axaml` is the only place colours, type sizes, radii and control sizes are
   defined. Palette: navy/anthracite surfaces in four steps, off-white text in three steps, **amber** (`#F29C38`)
   for the one primary action of an area, **cool blue** (`#5BA8E5`) for selection and information, and distinct
   success/warning/danger colours. Primary text is 14:1 against panels, tertiary text 4.7:1.
2. **Fluent stays, re-skinned.** Fluent's dark palette (`ColorPaletteResources`) is mapped onto the tokens, so
   controls we do not style by hand (drop-downs, menus, check boxes, scroll bars, focus rings) match. Fluent's accent
   is the blue; the amber is applied only through the `primary` button class. No new UI package.
3. **One class vocabulary** in `Themes/Controls.axaml`: buttons `primary · danger · warning · ghost · icon · large`,
   text `h1 · h2 · overline · caption · muted · tertiary · mono · value · value-lg · display`, containers
   `section · tile · toolbar · badge · alert`, `RadioButton.nav`, `ListBox.rows`. Views use classes and token brushes,
   never hex values (the map's Mapsui styles are the one exception, documented next to them).
4. **Line icons drawn for the project** (`Themes/Icons.axaml`, 24 × 24 stroke geometries) rendered by a small
   `StrokeIcon` control that takes the inherited text colour, so icons follow hover/disabled states. No icon font or
   third-party icon licence.
5. **Operations-console layout** for the main window: a navigation rail (Flight, Mission; Ctrl+1 / Ctrl+2), a top
   bar with page title, the selected vehicle at a glance (link, armed, mode, GPS, battery), backend state and an
   account menu; the map gets every pixel not needed by a 400 px side panel. Panels are flat sections separated by
   hairlines, not floating cards. Map tools (follow, add waypoints) float on the map; waypoint mode shows a banner.
6. **Two-pane sign-in**: a quiet brand pane (product name only, no invented organisation; range-ring motif drawn in
   vector, no stock image) and a focused form with labelled fields, per-field messages, password reveal, a busy
   state and the server's own message. The authentication flow is unchanged.
7. **States are explicit**: empty vehicle list and mission, "no telemetry yet", busy bar while refreshing, dismissable
   backend errors, link rows marked "not live" and the top strip dimmed while the backend connection is down.
8. **Safety affordances**: ARM/TAKEOFF still need confirmation; the dialog's focus starts on Cancel and Enter never
   confirms. LAND and RTL use the outlined warning style so they read differently from routine commands.
9. **No motion** beyond Fluent's pressed scale: state changes are instant, which also respects an operating-system
   "reduce motion" setting without needing to read it.
10. **English UI kept.** The product, its API messages and its tests are in English; translating the UI is a separate
    decision (it would need resource files), not part of a visual redesign.

## Consequences

* A new view or control is styled by choosing classes; changing a colour is a one-line token change.
* The top bar hides secondary details below 1380 px instead of clipping them (`Window.compact`).
* Tests still cover the view models; the new state (`IsStale`, `CurrentPage`, field errors) is unit tested. Screens are
  verified by rendering the real windows against the running backend (Avalonia headless + Skia) and looking at them.
* OpenStreetMap tiles stay light in a dark UI. A dark tile style needs another tile provider and its terms; it is
  left for the offline-tiles work (ADR-012).

## Alternatives considered

* **A UI kit (Semi.Avalonia, Material.Avalonia, FluentAvalonia)**: faster to start, but a second visual language to
  fight and another dependency to keep in step with Mapsui's Avalonia version (ADR-012).
* **Icon font (Material Symbols, Font Awesome)**: more glyphs, but a font asset and a licence for the ~40 icons we
  need; stroke geometries are a few lines each.
* **Keeping tabs instead of a rail**: tabs cost a row of vertical space and do not scale past a few pages.
