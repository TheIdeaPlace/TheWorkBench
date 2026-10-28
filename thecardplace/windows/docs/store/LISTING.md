# Microsoft Store listing: draft

Text for each field of the Partner Center listing. Paste it in, then edit freely; nothing reads
this file. The README's "The Microsoft Store" section covers the package.

## Before the first submission

1. Create the product in Partner Center as an **"MSIX or PWA app"** (an "EXE or MSI app" has no
   package identity and can't take this bundle), and reserve the name **The Card Place**.
2. Copy **Name** and **Publisher** from *Product management ▸ Product identity* into
   `build/store/identity.json`. If the reserved name has no spaces, change both DisplayNames in
   `build/store/AppxManifest.xml` to match.
3. The privacy page names only the iPhone and iPad app. Add Windows to its first line before
   submitting.

## Product name

The Card Place

## Short description

Hearts, Euchre, Spades, Cribbage and Sheephead, built for the keyboard and the screen reader.

## Description

The Card Place is five classic card games against the computer, designed from the start for people who play with a screen reader, a keyboard, or both: Hearts, Euchre, Spades, Cribbage and Sheephead.

Every card, play and score is in words. Each card says what it is and what it means in the game. In Euchre, the Jack of Clubs says it's the left bower, the second highest trump, and counts as spades. A card you can't play stays in your hand and tells you why: "you must follow clubs". Cribbage reads every count in its parts, and Sheephead keeps the secret partner secret until the Jack of Diamonds is played.

Your hand is a single stop. The arrow keys move card by card, and Enter plays. The same letters as The Card Place on the web read your hand, the trick, the last trick and the scores at any time. When it becomes your turn, focus moves to the first card you can play, or to your bid.

You decide how much you hear. Turn off other players' plays and catch up with the review keys instead, or turn off hints once you know the games. The computer players move at the pace you choose, from immediate to waiting for you to press Continue.

The table is drawn for everyone: a green felt, large cards with the suit always printed, and a clear amber focus ring. Windows high contrast themes and zoom are followed throughout.

The Card Place collects nothing about you. It never connects to the internet, and has no accounts, advertising or tracking.

## What's new in this version

First release in the Microsoft Store.

## Product features

(Up to 20, each 200 characters or fewer.)

- Five games: Hearts, Euchre, Spades, Cribbage and Sheephead, against the computer
- Every card, play, trick and score in words, for JAWS, NVDA and Narrator
- Your hand is one Tab stop; arrow keys move card by card, Enter plays
- A card you can't play says why, in the words of the rule
- Review keys read your hand, the trick, the last trick and the scores at any time
- Choose what you hear: plays, scores, hints and turns can each be turned off
- The computer's pace is yours, from immediate to waiting for you
- Easy, Normal and Hard computer players in Euchre, Cribbage and Sheephead
- Rule options: points to win, stick the dealer, going alone, doublers, leasters
- Follows Windows high contrast themes, and zooms to 200%
- Entirely offline; no accounts, advertising or tracking

## Keywords

(Up to 7.)

card games, hearts, euchre, spades, cribbage, accessibility, screen reader

## Category

Games ▸ Card & board

## Privacy policy URL

https://theideaplace.github.io/TheWorkBench/thecardplace/privacy.html

## Website

https://theideaplace.net/projects

## Age rating

The questionnaire answers: no violence, no gambling with real money, no user interaction or
sharing, no purchases. Card games with no wagering are normally rated for everyone.

## Screenshots

At least one, 1366×768 or larger. Take them in the test VM with vmtest `shot`: the hub, a hearts
trick in progress, the Spades bid, a cribbage count, and Settings ▸ Screen Reader Announcements.
The alt text for each says what is on the table.
