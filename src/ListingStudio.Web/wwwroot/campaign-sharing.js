window.listingStudioCampaign = {
    shareStillPost: async function (url, caption, fileName) {
        const response = await fetch(url, { credentials: "same-origin" });
        if (!response.ok) {
            throw new Error("The campaign image could not be opened.");
        }

        const file = new File([await response.blob()], fileName, { type: "image/jpeg" });
        if (navigator.share && navigator.canShare && navigator.canShare({ files: [file] })) {
            await navigator.share({ files: [file], text: caption, title: "Creative Listing Studio property post" });
            return "shared";
        }

        if (navigator.clipboard) {
            await navigator.clipboard.writeText(caption);
            return "copied";
        }

        return "unsupported";
    },

    copyText: async function (text) {
        if (!navigator.clipboard) {
            return false;
        }

        await navigator.clipboard.writeText(text);
        return true;
    }
};
