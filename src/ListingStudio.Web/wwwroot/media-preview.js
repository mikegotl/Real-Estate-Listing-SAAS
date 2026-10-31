window.listingStudioMedia = {
    setPlaybackRate(element, rate) {
        if (!(element instanceof HTMLMediaElement)) {
            return;
        }

        const playbackRate = Number(rate);
        const supported = Number.isFinite(playbackRate) && playbackRate >= 0.5 && playbackRate <= 2;
        element.defaultPlaybackRate = supported ? playbackRate : 1;
        element.playbackRate = element.defaultPlaybackRate;
    }
};
