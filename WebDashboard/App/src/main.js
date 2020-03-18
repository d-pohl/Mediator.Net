import 'typeface-roboto/index.css'
import 'material-design-icons-iconfont/dist/material-design-icons.css'
import '@mdi/font/css/materialdesignicons.css'
import Vue from 'vue'
import vuetify from './plugins/vuetify'
import App from './App'
import axios from "axios";
import globalState from "./Global.js";

Vue.config.productionTip = false

window.dashboardApp = new Vue({
  el: '#app',
  vuetify,
  render: h => h(App),
  methods: {
    sendViewRequest(request, payload, successHandler) {
      this.doSendViewRequest(request, payload, successHandler, 'text')
    },
    sendViewRequestBlob(request, payload, successHandler) {
      this.doSendViewRequest(request, payload, successHandler, 'blob')
    },
    doSendViewRequest(request, payload, successHandler, responseType) {
      const config = { // suppress auto conversion of string to JSON, otherwise strange behavior
         transformResponse: [function (data) { return data; }],
         responseType
      };
      globalState.busy = true;
      axios.post('/viewRequest/' + request + "?" + this.getDashboardViewContext(), payload, config)
        .then(function (response) {
            globalState.busy = false;
            successHandler(response.data);
        })
        .catch(function (error) {
            globalState.busy = false;
            if (error.response && error.response.data) {
               const data = JSON.parse(error.response.data);
               if (data.error) {
                  alert("Request failed: " + data.error);
               }
               else {
                  alert("Request failed: " + error.response.data);
               }
            }
            else {
               alert("Connection error: " + error);
            }
        });
    },
    getDashboardViewContext() {
      return globalState.sessionID + '_' + globalState.currentViewID;
    },
    registerViewEventListener(listener) {
      globalState.eventListener = listener;
    },
    showTimeRangeSelector(show) {
       if (show) {
         globalState.showTimeRangeSelector = true;
       }
       else {
         globalState.showTimeRangeSelector = false;
       }
    },
    getCurrentTimeRange() {
       return Object.assign({}, globalState.timeRange);
    },
    registerTimeRangeListener(listener) {
      globalState.timeRangeListener = listener;
    }
  }
})