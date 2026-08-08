PodoBot v0.4.0 patch

Replace existing files / add new files under src/PodoBot.

Included changes:
1. Multi roulette: name, command, response, permissions, independent items.
2. Reroll item: mark an item with '한 번 더' checkbox. It automatically spins again.
3. Roulette result chat is sent only after the final spin animation ends.
4. Reroll animation no longer disappears because old hide timers are cancelled.
5. Donation roulette rules: minimum amount + keyword + target roulette.
6. Repeating messages renamed to '반복 안내'.
7. New countdown timer: hours/minutes/seconds, OBS overlay, hides 10 seconds after finish.
8. Existing numeric counter renamed in UI to '횟수 카운터'.
9. Local karaoke song book: provider/number/title/artist and !노래책 / !노래검색.
10. Existing single roulette and old repeating-message settings are migrated automatically.

Required CHZZK Developers scope for donation rules:
- 후원 조회

After adding the scope, log out of PodoBot and authorize again.

OBS URLs:
- Roulette: http://localhost:18766/roulette
- Timer: http://localhost:18766/timer
