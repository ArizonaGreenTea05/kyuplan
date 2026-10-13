/* Helpers of the pickers (EventPicker.razor, SubGroupPicker.razor). */
window.ppPicker = {
    /* Enter must not click the trigger the focus returns to, and the arrows must not move the caret of the search box
       (or scroll the page, for the list of the subgroup picker, which also takes Space and Home/End).
       Blazor decides on preventDefault when it renders, not per key, so the element does it for these keys itself. */
    trapKeys(input, keys = ["Enter", "ArrowUp", "ArrowDown", "PageUp", "PageDown"]) {
        if (!input) return;
        input.addEventListener("keydown", e => { if (keys.includes(e.key)) e.preventDefault(); });
    },

    /* Scrolls the list so the highlighted entry is visible. With center, the entry is put in the middle of the list:
       that is how it opens at the chosen event. */
    reveal(list, index, center) {
        const item = list && list.children[index];
        if (!item) return;

        const top = item.offsetTop;
        const bottom = top + item.offsetHeight;
        if (center) list.scrollTop = top - (list.clientHeight - item.offsetHeight) / 2;
        else if (top < list.scrollTop) list.scrollTop = top;
        else if (bottom > list.scrollTop + list.clientHeight) list.scrollTop = bottom - list.clientHeight;
    },
};
