#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-service-items-planning-plugin/ServiceTimePlanningPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-service-items-planning-plugin/ServiceTimePlanningPlugin
fi

cp -av Documents/workspace/microting/eform-debian-service/Plugins/ServiceTimePlanningPlugin Documents/workspace/microting/eform-service-items-planning-plugin/ServiceTimePlanningPlugin
