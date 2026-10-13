/* Helpers of the comment editor (MemberComments.razor): what is selected in its text box, and selecting something afterwards.
   The style buttons need both: they put markers around the selection and then select the same words again. A text box keeps
   its selection when a button takes the focus, so it can be read when the button is pressed. */
window.ppComments = {
    /* [start, end] of the selection, as positions in the text. */
    selection(box) {
        return box ? [box.selectionStart, box.selectionEnd] : [0, 0];
    },

    /* Selects a stretch of the text and puts the focus back into the box. */
    select(box, start, end) {
        if (!box) return;
        box.focus();
        box.setSelectionRange(start, end);
    },
};
