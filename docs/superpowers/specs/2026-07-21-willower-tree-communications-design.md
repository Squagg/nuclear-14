# Willower Tree Communications Design

## Goal

Make the Tree of Life the Willowers' leadership communication point without adding a radio. Willowers Shamans and the Chieftan can use it to send a private faction announcement and open a tactical map that follows Willower identification items.

## Interaction

- Activating the Tree of Life opens the existing wasteland tactical-map interface.
- Its right-click menu includes **Announce to the Willowers**.
- Only the `TribalShaman` and `TribalElder` jobs can open either interface.
- The server repeats the job check when opening the map and when accepting an announcement message.

## Announcements

The Tree reuses the existing smoke-signal message composer and delivery system. Its component configuration disables activation-based composer opening so normal activation remains available to the TacMap.

The Tree uses the existing 128-character limit and a five-minute cooldown shared by everyone using that Tree. A submitted message is delivered only to living players whose current job belongs to the `Tribe` department. This includes `SyntheticProtectronTribal`. Non-Willowers receive no message or nearby bystander notice.

Empty messages, unauthorized submissions, and submissions during cooldown are rejected server-side. The sender's current job is checked again rather than trusting that they previously opened the composer.

## Tactical Map

Add a `Tribe` tactical feed to the existing wasteland-map system. The feed follows entities with a new `IdCardTribe` tag and uses the current 2.5-second map refresh interval.

Apply the tag to these Willower identification items:

- `N14IDTribeBossPendant`
- `N14IDTribeSawbonePendant`
- `N14IDTribeEnforcerPendant`
- `N14IDTribeBulletsPendant`

The tribal super mutant already receives `N14IDTribeBulletsPendant`, so it needs no special tracking path.

Create a tribal-only child of `MisfitsRobotNavCardRobCo`, add `IdCardTribe` to it, and load it only into `N14PlayerSyntheticProtectronTribal`. Other RobCo navigation cards remain untagged.

Each blip uses the identification item's assigned character name and job title and a Willower-colored marker. Tracking follows the item itself, so a dropped or stolen pendant or navigation card appears at its actual location. The existing shared annotation tools remain available.

## Compatibility

Role allowlists on the smoke-signal and wasteland-map components are optional. Existing bonfires, signal fires, tactical maps, Pip-Boy feeds, and robot navigation cards retain their current behavior when no allowlist or Tribe feed is configured.

No radio channel, headset, encryption key, or new standalone UI is added.

## Verification

Focused automated coverage will verify:

- Shaman and Chieftan access while another Willower job is denied.
- Tree announcements reach Willowers, including the tribal Protectron, and do not reach non-Willowers.
- The Tribe feed includes tagged pendants and the tribal Protectron navigation card while ignoring an ordinary RobCo navigation card.
- Prototype validation, focused tests, builds, and `git diff --check` pass.
