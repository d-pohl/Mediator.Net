import 'typeface-roboto/index.css'
import 'material-design-icons-iconfont/dist/material-design-icons.css'
import '@mdi/font/css/materialdesignicons.css'
import Vue from 'vue'
import vuetify from '../plugins/vuetify'
import ViewPages from './ViewPages.vue'

Vue.config.productionTip = false

import { setupDashboardEnv } from '../debug'

if (process.env.NODE_ENV === 'development') {
  setupDashboardEnv('pages')
}

const app = new Vue({
  el: '#app',
  vuetify,
  render(h) {
    return h(ViewPages)
  },
})