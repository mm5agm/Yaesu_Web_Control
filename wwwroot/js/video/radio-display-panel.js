/**
 * Owns Radio Display DOM: show/hide, status badge, fit/fill, fullscreen.
 * Transport (img.src = MJPEG URL) is handled by radio-display-ui.js.
 */
export class RadioDisplayPanel {
  /**
   * @param {string} imgId
   * @param {string} containerId
   * @param {string} badgeId
   * @param {{ ignoreHidePreference?: boolean, naturalSize?: boolean }} [options]
   */
  constructor(imgId, containerId, badgeId, options = {}) {
    this._img = document.getElementById(imgId);
    this._container = document.getElementById(containerId);
    this._badge = document.getElementById(badgeId);
    this._status = 'unconfigured';
    /** Pop-out must not share Index Hide via localStorage. */
    this._ignoreHidePreference = !!options.ignoreHidePreference;
    /** Index: size to video aspect ratio; pop-out: fill the window. */
    this._naturalSize = !!options.naturalSize;
    this._fitMode = localStorage.getItem('ywc.radioDisplayFit') || 'contain';
    this._applyFit();
  }

  /** @param {'unconfigured'|'idle'|'connecting'|'streaming'|'disconnected'|'error'} status */
  setStatus(status, detail) {
    this._status = status || 'idle';
    if (!this._container) return;

    const hiddenByUser = this.isHiddenByUser();
    const showRow = document.getElementById('radioDisplayShowRow');

    if (status === 'unconfigured') {
      this._container.style.display = 'none';
      if (showRow) showRow.style.display = 'none';
    } else if (hiddenByUser) {
      this._container.style.display = 'none';
      if (showRow) showRow.style.display = '';
    } else {
      this._container.style.display = '';
      if (showRow) showRow.style.display = 'none';
    }

    if (this._badge) {
      const labels = {
        unconfigured: 'Off',
        idle: 'Idle',
        connecting: 'Connecting…',
        streaming: 'Streaming',
        disconnected: 'Disconnected',
        error: 'Error'
      };
      this._badge.textContent = detail
        ? `${labels[status] || status}: ${detail}`
        : (labels[status] || status);
      this._badge.className = 'badge ' + (
        status === 'streaming' ? 'bg-success' :
        status === 'connecting' ? 'bg-warning text-dark' :
        status === 'disconnected' || status === 'error' ? 'bg-danger' :
        'bg-secondary'
      );
    }
  }

  show() {
    if (!this._ignoreHidePreference) {
      localStorage.setItem('ywc.radioDisplayVisible', '1');
    }
    const showRow = document.getElementById('radioDisplayShowRow');
    if (showRow) showRow.style.display = 'none';
    if (this._status !== 'unconfigured' && this._container) {
      this._container.style.display = '';
    }
  }

  hide() {
    if (this._ignoreHidePreference) return;
    localStorage.setItem('ywc.radioDisplayVisible', '0');
    if (this._container) this._container.style.display = 'none';
    const showRow = document.getElementById('radioDisplayShowRow');
    if (showRow && this._status !== 'unconfigured') showRow.style.display = '';
  }

  isHiddenByUser() {
    if (this._ignoreHidePreference) return false;
    return localStorage.getItem('ywc.radioDisplayVisible') === '0';
  }

  setFitMode(mode) {
    this._fitMode = mode === 'cover' ? 'cover' : 'contain';
    localStorage.setItem('ywc.radioDisplayFit', this._fitMode);
    this._applyFit();
  }

  toggleFitMode() {
    this.setFitMode(this._fitMode === 'contain' ? 'cover' : 'contain');
    return this._fitMode;
  }

  getFitMode() {
    return this._fitMode;
  }

  _applyFit() {
    if (!this._img) return;
    const body = this._container?.querySelector('.radio-display-body')
      || this._img.parentElement;

    this._img.style.objectFit = this._fitMode;
    this._img.style.display = 'block';
    this._img.style.margin = '0 auto';
    this._img.style.background = '#000';

    if (this._naturalSize) {
      // Index: keep radio aspect ratio; don't stretch into a tall black box.
      if (this._fitMode === 'contain') {
        if (body) {
          body.style.minHeight = '';
          body.style.height = '';
        }
        this._img.style.width = 'auto';
        this._img.style.maxWidth = '100%';
        this._img.style.height = 'auto';
        this._img.style.maxHeight = '50vh';
      } else {
        // Fill: crop to a fixed band on Index.
        if (body) {
          body.style.minHeight = '240px';
          body.style.height = '40vh';
        }
        this._img.style.width = '100%';
        this._img.style.maxWidth = '100%';
        this._img.style.height = '100%';
        this._img.style.maxHeight = 'none';
      }
      return;
    }

    // Pop-out / fullscreen-style: fill the available card body.
    this._img.style.width = '100%';
    this._img.style.maxWidth = '100%';
    this._img.style.height = '100%';
    this._img.style.maxHeight = 'none';
  }

  /**
   * @param {string} url
   */
  setStreamUrl(url) {
    if (!this._img) return;
    this._img.src = url || '';
  }

  clearStream() {
    if (!this._img) return;
    this._img.onload = null;
    this._img.onerror = null;
    this._img.onstalled = null;
    this._img.onabort = null;
    this._img.removeAttribute('src');
  }

  async requestFullscreen() {
    const el = this._container?.querySelector('.card') || this._container;
    if (!el) return;
    if (document.fullscreenElement) {
      await document.exitFullscreen();
    } else if (el.requestFullscreen) {
      await el.requestFullscreen();
    }
  }
}
