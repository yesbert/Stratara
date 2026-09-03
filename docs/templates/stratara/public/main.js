const CONSENT_KEY = 'stratara-consent'
const UMAMI_SRC = 'https://cloud.umami.is/script.js'
const UMAMI_WEBSITE_ID = '4a723469-6246-491c-add3-8804d9486f15'
const BANNER_ID = 'stratara-consent-banner'

// Analytics is loaded only after an explicit yes, and the answer is remembered in
// localStorage so the banner does not ask again. Declining and withdrawing both take
// the script back out; withdrawing reloads so nothing it already registered survives.
function readConsent() {
  try {
    return localStorage.getItem(CONSENT_KEY)
  } catch {
    return null
  }
}

function writeConsent(value) {
  try {
    localStorage.setItem(CONSENT_KEY, value)
  } catch {
    /* a browser that refuses storage simply gets asked again next time */
  }
}

function clearConsent() {
  try {
    localStorage.removeItem(CONSENT_KEY)
  } catch {
    /* nothing to clear if storage is unavailable */
  }
}

function loadAnalytics() {
  if (document.querySelector(`script[src="${UMAMI_SRC}"]`)) return
  const script = document.createElement('script')
  script.defer = true
  script.src = UMAMI_SRC
  script.setAttribute('data-website-id', UMAMI_WEBSITE_ID)
  document.head.appendChild(script)
}

function unloadAnalytics() {
  document.querySelectorAll(`script[src="${UMAMI_SRC}"]`).forEach(s => s.remove())
  try {
    delete window.umami
  } catch {
    /* the property may be non-configurable; the reload below settles it */
  }
}

function buildBanner() {
  const banner = document.createElement('div')
  banner.id = BANNER_ID
  banner.setAttribute('role', 'region')
  banner.setAttribute('aria-label', 'Analytics notice')
  banner.hidden = true
  banner.innerHTML = `
    <div class="st-consent-panel">
      <p>
        We would like to use Umami, a privacy-friendly analytics tool, to see how this site is used:
        anonymously, without cookies, and only if you agree. Details are in our
        <a href="/legal/privacy.html">privacy policy</a>.
      </p>
      <div class="st-consent-actions">
        <button type="button" class="btn btn-outline-secondary btn-sm" data-consent-decline>Decline</button>
        <button type="button" class="btn btn-primary btn-sm" data-consent-accept>Accept</button>
      </div>
    </div>`
  document.body.appendChild(banner)
  return banner
}

function showBanner(visible) {
  const banner = document.getElementById(BANNER_ID) ?? buildBanner()
  banner.hidden = !visible
}

function initConsent() {
  const consent = readConsent()
  if (consent === 'accepted') loadAnalytics()
  showBanner(consent !== 'accepted' && consent !== 'declined')

  document.addEventListener('click', event => {
    const target = event.target instanceof Element ? event.target : null
    if (target === null) return

    if (target.closest('[data-consent-accept]')) {
      writeConsent('accepted')
      loadAnalytics()
      showBanner(false)
    } else if (target.closest('[data-consent-decline]')) {
      writeConsent('declined')
      showBanner(false)
    } else if (target.closest('[data-consent-open]')) {
      showBanner(true)
    } else if (target.closest('[data-consent-revoke]')) {
      event.preventDefault()
      clearConsent()
      unloadAnalytics()
      location.reload()
    }
  })
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', initConsent)
} else {
  initConsent()
}

export default {
  iconLinks: [
    {
      icon: 'github',
      href: 'https://github.com/yesbert/Stratara',
      title: 'GitHub'
    },
    {
      icon: 'box-seam',
      href: 'https://www.nuget.org/packages?q=Stratara',
      title: 'NuGet'
    }
  ]
}
