// FT8 Tiles (issue #332): which station sits in which tile.
//
// A tile keeps its station from one poll to the next, so an operator who is
// slow to reach a tile, by switch scanning or eye gaze, doesn't find another
// station there when they get to it. A new station takes the first empty
// tile. A station the server stops listing frees its tile.
//
// Pure logic: no DOM, no fetch.

export class TileSlots {
    constructor() {
        /** @type {(string|null)[]} callsign per slot, null = empty */
        this.slots = [];
    }

    /**
     * @param {string[]} calls the stations the server lists now, in its order
     * @returns {(string|null)[]} the slot array after the update
     */
    update(calls) {
        const wanted = new Set(calls);
        // Free the tiles of stations no longer listed.
        this.slots = this.slots.map(c => (c !== null && wanted.has(c) ? c : null));
        const placed = new Set(this.slots.filter(c => c !== null));
        for (const call of calls) {
            if (placed.has(call)) continue;
            const free = this.slots.indexOf(null);
            if (free >= 0) this.slots[free] = call;
            else this.slots.push(call);
            placed.add(call);
        }
        // Drop empty tiles from the end so the page count shrinks again.
        while (this.slots.length > 0 && this.slots[this.slots.length - 1] === null) this.slots.pop();
        return this.slots;
    }

    clear() {
        this.slots = [];
    }

    pageCount(perPage) {
        return Math.max(1, Math.ceil(this.slots.length / perPage));
    }

    page(index, perPage) {
        const out = this.slots.slice(index * perPage, (index + 1) * perPage);
        while (out.length < perPage) out.push(null);
        return out;
    }
}
