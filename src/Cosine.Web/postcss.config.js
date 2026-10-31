export default {
  plugins: {
    // Smart TVs (LG webOS, Tizen) ship Chromium builds too old for cascade
    // layers. An unsupported @layer is an unknown at-rule, so the browser
    // drops the whole block - and since Tailwind v4 emits *everything* inside
    // @layer theme/base/components/utilities, that means every rule in the
    // app disappears. Flatten the layers back into plain rules.
    '@csstools/postcss-cascade-layers': {},
  },
}
