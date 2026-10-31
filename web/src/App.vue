<script setup>
import ConfirmDialog from './components/ConfirmDialog.vue'
import SubmitDialog from './components/SubmitDialog.vue'
</script>

<template>
  <router-view />
  <SubmitDialog />
  <ConfirmDialog />
</template>
