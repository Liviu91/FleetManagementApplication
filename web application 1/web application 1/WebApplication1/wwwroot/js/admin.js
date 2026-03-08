// Dynamically load Leaflet if not available
console.log('[admin.js] Loaded v2 - ' + new Date().toISOString());
function loadLeaflet(callback) {
    if (typeof L !== 'undefined') {
        callback();
        return;
    }
    
    // Load CSS first
    var link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = '/lib/leaflet/leaflet.css';
    document.head.appendChild(link);
    
    // Load JS
    var script = document.createElement('script');
    script.src = '/lib/leaflet/leaflet.js';
    script.onload = function() {
        callback();
    };
    script.onerror = function() {
        console.error('Failed to load Leaflet from /lib/leaflet/leaflet.js, trying CDN...');
        // Try CDN as fallback
        var cdnScript = document.createElement('script');
        cdnScript.src = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.js';
        cdnScript.onload = function() {
            callback();
        };
        cdnScript.onerror = function() {
            console.error('Failed to load Leaflet from CDN too!');
        };
        document.head.appendChild(cdnScript);
        
        var cdnCss = document.createElement('link');
        cdnCss.rel = 'stylesheet';
        cdnCss.href = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';
        document.head.appendChild(cdnCss);
    };
    document.head.appendChild(script);
}

// Initialize Leaflet on page load
loadLeaflet(function() {});

function showAddUserModal() {
        Swal.fire({
            title: 'Add User',
            html: `
                    <form id="addUserForm">
                        <div class="form-group">
                            <label for="add-display-name">Display Name</label>
                            <input type="text" id="add-display-name" class="swal2-input" placeholder="Mihai Popescu">
                        </div>
                        <div class="form-group">
                            <label for="add-email">Email</label>
                            <input type="email" id="add-email" class="swal2-input" placeholder="mihai.popescu@company.com">
                        </div>
                        <div class="form-group">
                            <label for="add-password">Password (optional, default: Pass1234!)</label>
                            <input type="password" id="add-password" class="swal2-input" placeholder="Leave empty for default">
                        </div>
                        <div class="form-group">
                            <label for="add-role">Role</label>
                            <select id="add-role" class="swal2-input">
                                <option value="Driver">Driver</option>
                                <option value="Admin">Admin</option>
                            </select>
                        </div>
                    </form>
                `,
            focusConfirm: false,
            preConfirm: () => {
                const displayName = document.getElementById('add-display-name').value;
                const email = document.getElementById('add-email').value;
                
                if (!displayName || !email) {
                    Swal.showValidationMessage('Display Name and Email are required');
                    return false;
                }
                
                return {
                    displayName: displayName,
                    email: email,
                    password: document.getElementById('add-password').value || null,
                    role: document.getElementById('add-role').value
                }
            }
        }).then(result => {
            if (result.isConfirmed) {
                var userData = result.value;

                $.ajax({
                    url: "/addUser",
                    type: "POST",
                    data: JSON.stringify(userData),
                    contentType: "application/json",
                    success: function (response) {
                        Swal.fire('Saved!', response.message || 'User has been added!', 'success');
                        loadDrivers(); // Refresh the drivers table
                        loadDashboardStats(); // Refresh stats
                    },
                    error: function (xhr) {
                        var errorMsg = 'There was an error adding the user.';
                        if (xhr.responseJSON && xhr.responseJSON.error) {
                            errorMsg = xhr.responseJSON.error;
                        }
                        Swal.fire('Error!', errorMsg, 'error');
                    }
                });
            }
        });
    }

    function showAddCarModal() {
        Swal.fire({
            title: 'Add Car',
            html: `
                    <form id="addCarForm">
                        <div class="form-group">
                            <label for="swal-input1">Serial Number</label>
                            <input type="text" id="add-serial-number" class="swal2-input" placeholder="SN000">
                        </div>
                    </form>
                `,
            focusConfirm: false,
            preConfirm: () => {
                return {
                    serialNumber: document.getElementById('add-serial-number').value,
                }
            }
        }).then(result => {
            if (result.isConfirmed) {
                var carData = result.value;

                $.ajax({
                    url: "/addCar",
                    type: "POST",
                    data: JSON.stringify(carData),
                    contentType: "application/json",
                    success: function (response) {
                        Swal.fire('Saved!', 'Car has been added!', 'success');
                        loadCars(); // Refresh the cars table
                        loadDashboardStats(); // Refresh stats
                    },
                    error: function (error) {
                        Swal.fire('Error!', 'There was an error adding the car.', 'error');
                    }
                });
            }
        });
    }

    async function showAddRouteModal() {
        try {
            const [usersRes, carsRes] = await Promise.all([
                fetch('/getUsers'),
                fetch('/getCars')
            ]);

            const users = await usersRes.json();
            const cars = await carsRes.json();

            const userOptions = users.map(u => `<option value="${u.id}">${u.firstName} ${u.lastName}</option>`).join('');
            const carOptions = cars.map(c => `<option value="${c.id}">${c.serialNumber}</option>`).join('');

            Swal.fire({
                title: 'Add Route',
                html: `
                    <form id="addRouteForm">
                        <div class="form-group">
                            <label>User</label>
                            <select id="route-user" class="swal2-input">${userOptions}</select>
                        </div>
                        <div class="form-group">
                            <label>Car</label>
                            <select id="route-car" class="swal2-input">${carOptions}</select>
                        </div>
                        <div class="form-group">
                            <label>Route Name</label>
                            <input type="text" id="route-name" class="swal2-input" placeholder="Morning Route">
                        </div>
                        <div class="form-group">
                            <label>Start</label>
                            <input type="text" id="route-start" class="swal2-input" placeholder="Start location">
                        </div>
                        <div class="form-group">
                            <label>End</label>
                            <input type="text" id="route-end" class="swal2-input" placeholder="End location">
                        </div>
                        <div class="form-group">
                            <label>Start Date</label>
                            <input type="datetime-local" id="route-start-date" class="swal2-input">
                        </div>
                    </form>
                `,
                focusConfirm: false,
                preConfirm: () => {
                    return {
                        //userId: parseInt(document.getElementById('route-user').value),
                        userId: document.getElementById('route-user').value,
                        carId: parseInt(document.getElementById('route-car').value),
                        name: document.getElementById('route-name').value,
                        start: document.getElementById('route-start').value,
                        end: document.getElementById('route-end').value,
                        startDate: document.getElementById('route-start-date').value,
                        endDate: null // Explicitly set to null
                    }
                }
            }).then(result => {
                if (result.isConfirmed) {
                    const routeData = result.value;

                    $.ajax({
                        url: "/addRoute",
                        type: "POST",
                        data: JSON.stringify(routeData),
                        contentType: "application/json",
                        success: function () {
                            Swal.fire('Saved!', 'Route has been added!', 'success');
                            loadRoutes();
                            loadDashboardStats();
                        },
                        error: function (xhr) {
                            var errorMsg = 'There was an error adding the route.';
                            if (xhr.responseJSON && xhr.responseJSON.error) {
                                errorMsg = xhr.responseJSON.error;
                            }
                            Swal.fire('Error!', errorMsg, 'error');
                        }
                    });
                }
            });
        } catch (error) {
            Swal.fire('Error!', 'Failed to fetch users or cars.', 'error');
            console.error(error);
        }
    }

    // Load data on page load
    $(document).ready(function() {
        loadDashboardStats();
        
        setTimeout(() => { loadDrivers(); }, 100);
        setTimeout(() => { loadCars(); }, 200);
        setTimeout(() => { loadRoutes(); }, 300);
        
        setInterval(loadActiveVehicles, 5000);
        loadActiveVehicles();
        
        $('button[data-bs-toggle="tab"]').on('shown.bs.tab', function (e) {
            const target = $(e.target).attr('data-bs-target');
            if (target === '#drivers') loadDrivers();
            else if (target === '#vehicles') loadCars();
            else if (target === '#routes') loadRoutes();
            else if (target === '#monitoring') loadActiveVehicles();
        });
    });

    // Load dashboard statistics
    function loadDashboardStats() {
        Promise.all([
            fetch('/getUsers'),
            fetch('/getCars'),
            fetch('/getRoutes')
        ]).then(responses => Promise.all(responses.map(r => r.json())))
        .then(([users, cars, routes]) => {
            $('#totalDrivers').text(users.length);
            $('#totalVehicles').text(cars.length);
            $('#totalRoutes').text(routes.length);
            const activeRoutes = routes.filter(r => r.status === 'Started').length;
            $('#activeRoutes').text(activeRoutes);

            // Populate recent activity
            const tbody = $('#recentActivityTable');
            tbody.empty();
            const recent = routes.slice(0, 10);
            if (recent.length === 0) {
                tbody.append(`<tr><td colspan="5" class="text-center text-muted">No recent activity.</td></tr>`);
            } else {
                recent.forEach(route => {
                    const statusBadge = route.status === 'Started' ? 'success' : route.status === 'Finished' ? 'secondary' : 'warning';
                    tbody.append(`
                        <tr>
                            <td>${route.driverName}</td>
                            <td>${route.carSerialNumber}</td>
                            <td>${route.name}</td>
                            <td><span class="badge bg-${statusBadge}">${route.status}</span></td>
                            <td>${new Date(route.startDate).toLocaleString()}</td>
                        </tr>
                    `);
                });
            }
        }).catch(error => console.error('Error loading dashboard stats:', error));
    }

    // Load drivers table
    function loadDrivers() {
        const tbody = $('#driversTable tbody');
        
        // Show loading state
        tbody.html(`
            <tr>
                <td colspan="6" class="text-center">
                    <div class="spinner-border spinner-border-sm" role="status">
                        <span class="visually-hidden">Loading...</span>
                    </div>
                    Loading drivers...
                </td>
            </tr>
        `);
        
        fetch('/getUsers')
            .then(response => {
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                return response.json();
            })
            .then(users => {
                tbody.empty();
                
                if (users.length === 0) {
                    tbody.append(`
                        <tr>
                            <td colspan="6" class="text-center text-muted">No drivers found. Click "Add Driver" to create one.</td>
                        </tr>
                    `);
                    return;
                }
                
                users.forEach(user => {
                    tbody.append(`
                        <tr class="driver-row" data-driver-id="${user.id}">
                            <td>
                                <button class="btn btn-sm btn-link expand-btn" onclick="toggleDriverDetails('${user.id}')">
                                    <i class="bi bi-chevron-right"></i>
                                </button>
                            </td>
                            <td>${user.firstName}</td>
                            <td>${user.firstName}</td>
                            <td class="route-count-${user.id}">-</td>
                            <td class="active-route-count-${user.id}">-</td>
                            <td>
                                <button class="btn btn-sm btn-info" onclick="viewDriverDetails('${user.id}')">
                                    <i class="bi bi-eye"></i> View
                                </button>
                                <button class="btn btn-sm btn-warning" onclick="editDriver('${user.id}')">
                                    <i class="bi bi-pencil"></i> Edit
                                </button>
                                <button class="btn btn-sm btn-danger" onclick="deleteDriver('${user.id}')">
                                    <i class="bi bi-trash"></i> Delete
                                </button>
                            </td>
                        </tr>
                        <tr id="driver-details-${user.id}" class="driver-details-row" style="display: none;">
                            <td colspan="6">
                                <div class="p-3">
                                    <div class="spinner-border spinner-border-sm" role="status">
                                        <span class="visually-hidden">Loading...</span>
                                    </div>
                                    Loading driver details...
                                </div>
                            </td>
                        </tr>
                    `);
                });
            })
            .catch(error => {
                console.error('Error loading drivers:', error);
                tbody.html(`
                    <tr>
                        <td colspan="6" class="text-center text-danger">
                            <i class="bi bi-exclamation-triangle"></i> 
                            Error loading drivers: ${error.message}
                            <br><small>Check browser console for details</small>
                        </td>
                    </tr>
                `);
            });
    }

    // Load cars table
    function loadCars() {
        const tbody = $('#carsTable tbody');
        
        // Show loading state
        tbody.html(`
            <tr>
                <td colspan="5" class="text-center">
                    <div class="spinner-border spinner-border-sm" role="status">
                        <span class="visually-hidden">Loading...</span>
                    </div>
                    Loading vehicles...
                </td>
            </tr>
        `);
        
        fetch('/getCars', {
            credentials: 'include'  // Include cookies for authentication
        })
            .then(response => {
                if (!response.ok) {
                    throw new Error(`HTTP error! status: ${response.status}`);
                }
                return response.json();
            })
            .then(cars => {
                tbody.empty();
                
                if (cars.length === 0) {
                    tbody.append(`
                        <tr>
                            <td colspan="5" class="text-center text-muted">No vehicles found. Click "Add Vehicle" to create one.</td>
                        </tr>
                    `);
                    return;
                }
                
                cars.forEach(car => {
                    tbody.append(`
                        <tr class="car-row" data-car-id="${car.id}">
                            <td>
                                <button class="btn btn-sm btn-link expand-btn" onclick="toggleCarDetails(${car.id})">
                                    <i class="bi bi-chevron-right"></i>
                                </button>
                            </td>
                            <td>${car.serialNumber}</td>
                            <td class="route-count-car-${car.id}">-</td>
                            <td><span class="badge bg-success car-status-${car.id}">Available</span></td>
                            <td>
                                <button class="btn btn-sm btn-info" onclick="viewCarDetails(${car.id})">
                                    <i class="bi bi-eye"></i> View
                                </button>
                                <button class="btn btn-sm btn-warning" onclick="editCar(${car.id})">
                                    <i class="bi bi-pencil"></i> Edit
                                </button>
                                <button class="btn btn-sm btn-danger" onclick="deleteCar(${car.id})">
                                    <i class="bi bi-trash"></i> Delete
                                </button>
                            </td>
                        </tr>
                        <tr id="car-details-${car.id}" class="car-details-row" style="display: none;">
                            <td colspan="5">
                                <div class="p-3">
                                    <div class="spinner-border spinner-border-sm" role="status">
                                        <span class="visually-hidden">Loading...</span>
                                    </div>
                                    Loading car details...
                                </div>
                            </td>
                        </tr>
                    `);
                });
            })
            .catch(error => {
                console.error('Error loading cars:', error);
                tbody.html(`
                    <tr>
                        <td colspan="5" class="text-center text-danger">
                            <i class="bi bi-exclamation-triangle"></i>
                            Error loading vehicles: ${error.message}
                            <br><small>Check browser console for details</small>
                        </td>
                    </tr>
                `);
            });
    }

    // Load active vehicles for monitoring
    function loadActiveVehicles() {
        fetch('/getActiveVehicles')
            .then(response => response.json())
            .then(vehicles => {
                console.log('[monitoring] vehicles:', JSON.stringify(vehicles).substring(0, 500));
                const grid = $('#activeVehiclesGrid');
                grid.empty();
                
                if (vehicles.length === 0) {
                    grid.append('<div class="col-12"><p class="text-muted">No active vehicles at the moment.</p></div>');
                    return;
                }
                
                vehicles.forEach(vehicle => {
                    grid.append(`
                        <div class="col-md-6 mb-3">
                            <div class="card">
                                <div class="card-header bg-success text-white d-flex justify-content-between align-items-center">
                                    <div>
                                        <h5 class="mb-0"><i class="bi bi-truck"></i> ${vehicle.carSerialNumber}</h5>
                                        <small>Driver: ${vehicle.driverName} | Route: ${vehicle.routeName}</small>
                                    </div>
                                    <small>${new Date(vehicle.lastUpdate).toLocaleTimeString()}</small>
                                </div>
                                <div class="card-body p-2">
                                    <div class="row g-1 text-center">
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">RPM</small><strong style="font-size:1.2rem">${vehicle.rpm || 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Speed</small><strong style="font-size:1.2rem">${vehicle.speed || 'N/A'} km/h</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Throttle</small><strong>${vehicle.throttlePosition ? parseFloat(vehicle.throttlePosition).toFixed(1) + '%' : 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Engine Load</small><strong>${vehicle.engineLoad ? parseFloat(vehicle.engineLoad).toFixed(1) + '%' : 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Coolant Temp</small><strong>${vehicle.engineCoolantTemperature || 'N/A'}°C</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Intake Air</small><strong>${vehicle.intakeAirTemperature || 'N/A'}°C</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">MAF</small><strong>${vehicle.maf ? parseFloat(vehicle.maf).toFixed(2) + ' g/s' : 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">MAP</small><strong>${vehicle.map || 'N/A'} kPa</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Fuel Rail</small><strong>${vehicle.fuelRailPressure ? parseFloat(vehicle.fuelRailPressure).toFixed(0) + ' kPa' : 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">O₂ Sensor</small><strong>${vehicle.o2SensorVoltage ? parseFloat(vehicle.o2SensorVoltage).toFixed(3) + ' V' : 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Lambda (λ)</small><strong>${vehicle.lambdaValue ? parseFloat(vehicle.lambdaValue).toFixed(3) : 'N/A'}</strong></div></div>
                                        <div class="col-4"><div class="border rounded p-1"><small class="text-muted d-block">Catalyst Temp</small><strong>${vehicle.catalystTemperature || 'N/A'}°C</strong></div></div>
                                    </div>
                                </div>
                            </div>
                        </div>
                    `);
                });
            })
            .catch(error => {
                console.error('Error loading active vehicles:', error);
            });
    }

    // Track active route maps to clean up properly
    var routeMaps = {};

    // Load routes table
    function loadRoutes() {
        var tbody = $('#routesTableBody');
        tbody.html('<tr><td colspan="7" class="text-center"><div class="spinner-border spinner-border-sm"></div> Loading routes...</td></tr>');

        fetch('/getRoutes')
            .then(function(r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
            .then(function(routes) {
                tbody.empty();
                if (routes.length === 0) {
                    tbody.append('<tr><td colspan="7" class="text-center text-muted">No routes found. Click "Create Route" to add one.</td></tr>');
                    return;
                }
                routes.forEach(function(route) {
                    var statusBadge = route.status === 'Started' ? 'success' : route.status === 'Finished' ? 'secondary' : 'warning';
                    tbody.append(
                        '<tr class="route-row" data-route-id="' + route.id + '" data-status="' + route.status + '" data-assigned="' + route.isAssigned + '">' +
                            '<td>' +
                                '<button class="btn btn-sm btn-link expand-btn" onclick="toggleRouteDetails(' + route.id + ', ' + route.isAssigned + ')">' +
                                    '<i class="bi bi-chevron-right"></i>' +
                                '</button>' +
                            '</td>' +
                            '<td>' + route.name + '</td>' +
                            '<td>' + route.start + '</td>' +
                            '<td>' + route.end + '</td>' +
                            '<td>' + new Date(route.startDate).toLocaleString() + '</td>' +
                            '<td>' + (route.endDate ? new Date(route.endDate).toLocaleString() : '-') + '</td>' +
                            '<td><span class="badge bg-' + statusBadge + '">' + route.status + '</span></td>' +
                        '</tr>' +
                        '<tr id="route-details-' + route.id + '" class="route-details-row" style="display: none;">' +
                            '<td colspan="7">' +
                                '<div class="p-3">' +
                                    '<div class="spinner-border spinner-border-sm"></div> Loading route details...' +
                                '</div>' +
                            '</td>' +
                        '</tr>'
                    );
                });
            })
            .catch(function(error) {
                tbody.html('<tr><td colspan="7" class="text-center text-danger">Error loading routes: ' + error.message + '</td></tr>');
            });
    }

    // Filter routes by status
    function filterRoutes() {
        var filter = $('#statusFilter').val();
        $('#routesTableBody tr.route-row').each(function () {
            var status = $(this).data('status');
            var rid = $(this).data('route-id');
            var detailsRow = $('#route-details-' + rid);
            if (!filter || status === filter) {
                $(this).show();
            } else {
                $(this).hide();
                detailsRow.hide();
            }
        });
    }

    // Toggle route details
    function toggleRouteDetails(routeId, isAssigned) {
        var detailsRow = $('#route-details-' + routeId);
        var expandBtn = $('.route-row[data-route-id="' + routeId + '"] .expand-btn i');

        if (detailsRow.is(':visible')) {
            detailsRow.hide();
            expandBtn.removeClass('bi-chevron-down').addClass('bi-chevron-right');
            if (routeMaps[routeId]) {
                routeMaps[routeId].remove();
                delete routeMaps[routeId];
            }
        } else {
            detailsRow.show();
            expandBtn.removeClass('bi-chevron-right').addClass('bi-chevron-down');
            loadRouteDetailsInline(routeId, isAssigned);
        }
    }

    // Load route details inline
    function loadRouteDetailsInline(routeId, isAssigned) {
        var detailsRowId = '#route-details-' + routeId;
        var detailsCell = $(detailsRowId + ' td');

        // Clean up previous map if re-expanding
        if (routeMaps[routeId]) {
            routeMaps[routeId].remove();
            delete routeMaps[routeId];
        }

        var detailsUrl = '/getRouteDetails/' + routeId;
        var gpsUrl = '/getRouteGpsData/' + routeId;

        Promise.all([
            fetch(detailsUrl).then(function(r) { return r.json(); }),
            fetch(gpsUrl).then(function(r) { return r.json(); })
        ])
        .then(function(results) {
            var route = results[0];
            var gpsData = results[1];
            console.log('[routes] gpsData count:', gpsData ? gpsData.length : 0, 'sample:', gpsData && gpsData.length > 0 ? JSON.stringify(gpsData[0]) : 'none');

            if (!isAssigned) {
                detailsCell.html('<div class="p-4 text-center"><i class="bi bi-exclamation-circle text-warning" style="font-size: 2rem;"></i><h5 class="mt-2 text-muted">Not assigned</h5><p class="text-muted">This route has no driver or vehicle assigned yet.</p></div>');
                return;
            }

            var mapHtml = '';
            if (gpsData && gpsData.length > 0) {
                mapHtml = '<div class="row mt-3">' +
                    '<div class="col-md-8">' +
                        '<h6><i class="bi bi-geo-alt"></i> Route Map</h6>' +
                        '<p class="text-muted mb-2"><small>Click on any point along the route to see vehicle stats at that moment</small></p>' +
                        '<div id="routeMap-' + routeId + '" style="height:400px;width:100%;border:1px solid #dee2e6;border-radius:8px;background:#e9ecef;"></div>' +
                        '<p class="mt-1 text-muted"><small>' + gpsData.length + ' GPS points recorded</small></p>' +
                    '</div>' +
                    '<div class="col-md-4">' +
                        '<div class="telemetry-panel" id="telemetry-panel-' + routeId + '">' +
                            '<h6><i class="bi bi-speedometer2"></i> Driving Data</h6>' +
                            '<p class="text-muted"><small>Click a point on the map to view data</small></p>' +
                            '<div class="row mt-3">' +
                                '<div class="col-12 mb-3"><div class="card bg-light"><div class="card-body p-3 text-center"><div class="telemetry-label">RPM</div><div class="telemetry-value" id="tel-rpm-' + routeId + '" style="font-size:2rem">--</div></div></div></div>' +
                                '<div class="col-12 mb-3"><div class="card bg-light"><div class="card-body p-3 text-center"><div class="telemetry-label">Speed</div><div class="telemetry-value" id="tel-speed-' + routeId + '" style="font-size:2rem">-- km/h</div></div></div></div>' +
                                '<div class="col-12"><div class="card bg-light"><div class="card-body p-2 text-center"><div class="telemetry-label">Timestamp</div><div style="font-size:0.9rem;font-weight:600;" id="tel-time-' + routeId + '">--</div></div></div></div>' +
                            '</div>' +
                        '</div>' +
                    '</div>' +
                '</div>';
            } else {
                mapHtml = '<p class="text-muted mt-3">No GPS data available for this route.</p>';
            }

            var statusBadge = route.status === 'Started' ? 'success' : route.status === 'Finished' ? 'secondary' : 'warning';

            detailsCell.html(
                '<div class="p-3 bg-light">' +
                    '<div class="row mb-3">' +
                        '<div class="col-md-6">' +
                            '<h5><i class="bi bi-map"></i> ' + route.name + '</h5>' +
                            '<p class="mb-1"><strong>Driver:</strong> ' + route.driverName + '</p>' +
                            '<p class="mb-1"><strong>Vehicle:</strong> ' + route.carSerialNumber + '</p>' +
                        '</div>' +
                        '<div class="col-md-6">' +
                            '<p class="mb-1"><strong>From:</strong> ' + route.start + '</p>' +
                            '<p class="mb-1"><strong>To:</strong> ' + route.end + '</p>' +
                            '<p class="mb-1"><strong>Status:</strong> <span class="badge bg-' + statusBadge + '">' + route.status + '</span></p>' +
                        '</div>' +
                    '</div>' +
                    mapHtml +
                '</div>'
            );

            // Initialize map after DOM is updated
            if (gpsData && gpsData.length > 0) {
                setTimeout(function() {
                    if (typeof L === 'undefined') {
                        loadLeaflet(function() {
                            initRouteMapInline(routeId, gpsData);
                        });
                    } else {
                        initRouteMapInline(routeId, gpsData);
                    }
                }, 500);
            }
        })
        .catch(function(error) {
            console.error('Error loading route details:', error);
            detailsCell.html('<div class="alert alert-danger m-3">Failed to load route details.</div>');
        });
    }

    // Initialize inline Leaflet map with clickable GPS points
    function initRouteMapInline(routeId, gpsData) {
        var mapElId = 'routeMap-' + routeId;
        var mapEl = document.getElementById(mapElId);
        if (!mapEl) return;

        if (typeof L === 'undefined') {
            mapEl.innerHTML = '<div class="alert alert-danger">Map library failed to load. Please refresh the page.</div>';
            return;
        }

        try {
            var map = L.map(mapElId).setView([gpsData[0].lat, gpsData[0].lng], 13);
            routeMaps[routeId] = map;

            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                attribution: '\u00a9 OpenStreetMap contributors'
            }).addTo(map);

            // Route polyline
            var routeCoords = gpsData.map(function(p) { return [p.lat, p.lng]; });
            var polyline = L.polyline(routeCoords, { color: '#0d6efd', weight: 4, opacity: 0.8 }).addTo(map);

            // Start marker (green)
            L.marker([gpsData[0].lat, gpsData[0].lng], {
                icon: L.icon({
                    iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-green.png',
                    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-shadow.png',
                    iconSize: [25, 41], iconAnchor: [12, 41], popupAnchor: [1, -34], shadowSize: [41, 41]
                })
            }).addTo(map).bindPopup('<b>Start</b><br>' + new Date(gpsData[0].timestamp).toLocaleString());

            // End marker (red)
            var last = gpsData[gpsData.length - 1];
            L.marker([last.lat, last.lng], {
                icon: L.icon({
                    iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-red.png',
                    shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/1.9.4/images/marker-shadow.png',
                    iconSize: [25, 41], iconAnchor: [12, 41], popupAnchor: [1, -34], shadowSize: [41, 41]
                })
            }).addTo(map).bindPopup('<b>End</b><br>' + new Date(last.timestamp).toLocaleString());

            // Clickable circle markers for every GPS point
            gpsData.forEach(function(point, idx) {
                if (idx === 0 || idx === gpsData.length - 1) return;

                var marker = L.circleMarker([point.lat, point.lng], {
                    radius: 6,
                    fillColor: '#ff7800',
                    color: '#fff',
                    weight: 2,
                    opacity: 1,
                    fillOpacity: 0.9
                }).addTo(map);

                marker.on('click', function () {
                    $('#tel-rpm-' + routeId).text((point.rpm || 'N/A'));
                    $('#tel-speed-' + routeId).text((point.speed || 'N/A') + ' km/h');
                    $('#tel-time-' + routeId).text(new Date(point.timestamp).toLocaleString());

                    // Highlight selected marker
                    map.eachLayer(function(l) {
                        if (l instanceof L.CircleMarker && !(l instanceof L.Marker)) {
                            l.setStyle({ fillColor: '#ff7800', color: '#fff', radius: 6 });
                        }
                    });
                    marker.setStyle({ fillColor: '#dc3545', color: '#fff', radius: 9 });
                });

                marker.bindTooltip(new Date(point.timestamp).toLocaleTimeString() + ' \u2014 ' + (point.speed || '?') + ' km/h', { direction: 'top' });
            });

            map.fitBounds(polyline.getBounds(), { padding: [50, 50] });

            // Force Leaflet to recalculate size since container was hidden
            setTimeout(function() { map.invalidateSize(); }, 200);
        } catch (e) {
            console.error('Error initializing map:', e);
        }
    }

    // Delete functions
    function deleteDriver(id) {
        Swal.fire({
            title: 'Are you sure?',
            text: "This will delete the driver permanently!",
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#d33',
            cancelButtonColor: '#3085d6',
            confirmButtonText: 'Yes, delete it!'
        }).then((result) => {
            if (result.isConfirmed) {
                $.ajax({
                    url: `/deleteUser/${id}`,
                    type: 'DELETE',
                    success: function() {
                        Swal.fire('Deleted!', 'Driver has been deleted.', 'success');
                        loadDrivers();
                    },
                    error: function() {
                        Swal.fire('Error!', 'Failed to delete driver.', 'error');
                    }
                });
            }
        });
    }

    // Toggle driver details row
    function toggleDriverDetails(driverId) {
        const detailsRow = $(`#driver-details-${driverId}`);
        const expandBtn = $(`.driver-row[data-driver-id="${driverId}"] .expand-btn i`);
        
        if (detailsRow.is(':visible')) {
            detailsRow.hide();
            expandBtn.removeClass('bi-chevron-down').addClass('bi-chevron-right');
        } else {
            detailsRow.show();
            expandBtn.removeClass('bi-chevron-right').addClass('bi-chevron-down');
            
            // Load details if not already loaded
            if (detailsRow.data('loaded') !== true) {
                loadDriverDetailsInline(driverId);
            }
        }
    }

    // Load driver details inline
    function loadDriverDetailsInline(driverId) {
        const detailsCell = $(`#driver-details-${driverId} td`);
        
        fetch(`/getDriverDetails/${driverId}`)
            .then(response => response.json())
            .then(data => {
                $(`.route-count-${driverId}`).text(data.totalRoutes);
                $(`.active-route-count-${driverId}`).text(data.activeRoutes);
                
                let routesHtml = '';
                if (data.routes && data.routes.length > 0) {
                    routesHtml = `
                        <h6 class="mt-3">Recent Routes:</h6>
                        <div class="table-responsive">
                            <table class="table table-sm table-bordered">
                                <thead>
                                    <tr>
                                        <th>Route Name</th>
                                        <th>Vehicle</th>
                                        <th>Start</th>
                                        <th>End</th>
                                        <th>Status</th>
                                        <th>Date</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    ${data.routes.slice(0, 5).map(route => `
                                        <tr>
                                            <td>${route.name}</td>
                                            <td>${route.carSerialNumber}</td>
                                            <td>${route.start}</td>
                                            <td>${route.end}</td>
                                            <td><span class="badge bg-${route.status === 'Started' ? 'success' : route.status === 'Finished' ? 'secondary' : 'warning'}">${route.status}</span></td>
                                            <td>${new Date(route.startDate).toLocaleDateString()}</td>
                                        </tr>
                                    `).join('')}
                                </tbody>
                            </table>
                        </div>
                    `;
                } else {
                    routesHtml = '<p class="text-muted">No routes found for this driver.</p>';
                }
                
                let telemetryHtml = '';
                if (data.recentTelemetry && data.recentTelemetry.length > 0) {
                    const latest = data.recentTelemetry[0];
                    telemetryHtml = `
                        <h6 class="mt-3">Latest Telemetry Data:</h6>
                        <div class="row">
                            <div class="col-md-3">
                                <div class="card bg-light">
                                    <div class="card-body p-2">
                                        <small class="text-muted">Speed</small>
                                        <h5>${latest.speed || 'N/A'} km/h</h5>
                                    </div>
                                </div>
                            </div>
                            <div class="col-md-3">
                                <div class="card bg-light">
                                    <div class="card-body p-2">
                                        <small class="text-muted">RPM</small>
                                        <h5>${latest.rpm || 'N/A'}</h5>
                                    </div>
                                </div>
                            </div>
                            <div class="col-md-3">
                                <div class="card bg-light">
                                    <div class="card-body p-2">
                                        <small class="text-muted">Temperature</small>
                                        <h5>${latest.engineCoolantTemperature || 'N/A'}°C</h5>
                                    </div>
                                </div>
                            </div>
                            <div class="col-md-3">
                                <div class="card bg-light">
                                    <div class="card-body p-2">
                                        <small class="text-muted">Battery</small>
                                        <h5>${latest.batteryVoltage || 'N/A'}V</h5>
                                    </div>
                                </div>
                            </div>
                        </div>
                    `;
                } else {
                    telemetryHtml = '<p class="text-muted mt-3">No telemetry data available.</p>';
                }
                
                detailsCell.html(`
                    <div class="p-3 bg-light">
                        <div class="row">
                            <div class="col-md-12">
                                <h5><i class="bi bi-person-badge"></i> ${data.displayName}</h5>
                                <p><strong>Email:</strong> ${data.email}</p>
                                <div class="row">
                                    <div class="col-md-4">
                                        <p><strong>Total Routes:</strong> ${data.totalRoutes}</p>
                                    </div>
                                    <div class="col-md-4">
                                        <p><strong>Active Routes:</strong> ${data.activeRoutes}</p>
                                    </div>
                                    <div class="col-md-4">
                                        <p><strong>Completed:</strong> ${data.completedRoutes}</p>
                                    </div>
                                </div>
                                ${telemetryHtml}
                                ${routesHtml}
                            </div>
                        </div>
                    </div>
                `);
                
                $(`#driver-details-${driverId}`).data('loaded', true);
            })
            .catch(error => {
                console.error('Error loading driver details:', error);
                detailsCell.html(`
                    <div class="alert alert-danger m-3">
                        Failed to load driver details. Please try again.
                    </div>
                `);
            });
    }

    // View driver details in modal
    function viewDriverDetails(driverId) {
        fetch(`/getDriverDetails/${driverId}`)
            .then(response => response.json())
            .then(data => {
                let routesHtml = '';
                if (data.routes && data.routes.length > 0) {
                    routesHtml = `
                        <h6 class="mt-3">Routes (${data.routes.length}):</h6>
                        <div class="table-responsive" style="max-height: 300px; overflow-y: auto;">
                            <table class="table table-sm table-bordered">
                                <thead class="sticky-top bg-white">
                                    <tr>
                                        <th>Route Name</th>
                                        <th>Vehicle</th>
                                        <th>Status</th>
                                        <th>Date</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    ${data.routes.map(route => `
                                        <tr>
                                            <td>${route.name}</td>
                                            <td>${route.carSerialNumber}</td>
                                            <td><span class="badge bg-${route.status === 'Started' ? 'success' : route.status === 'Finished' ? 'secondary' : 'warning'}">${route.status}</span></td>
                                            <td>${new Date(route.startDate).toLocaleDateString()}</td>
                                        </tr>
                                    `).join('')}
                                </tbody>
                            </table>
                        </div>
                    `;
                }
                
                Swal.fire({
                    title: `Driver: ${data.displayName}`,
                    html: `
                        <div class="text-start">
                            <p><strong>Email:</strong> ${data.email}</p>
                            <div class="row mt-3">
                                <div class="col-4">
                                    <div class="card bg-primary text-white">
                                        <div class="card-body p-2">
                                            <small>Total Routes</small>
                                            <h4>${data.totalRoutes}</h4>
                                        </div>
                                    </div>
                                </div>
                                <div class="col-4">
                                    <div class="card bg-success text-white">
                                        <div class="card-body p-2">
                                            <small>Active</small>
                                            <h4>${data.activeRoutes}</h4>
                                        </div>
                                    </div>
                                </div>
                                <div class="col-4">
                                    <div class="card bg-secondary text-white">
                                        <div class="card-body p-2">
                                            <small>Completed</small>
                                            <h4>${data.completedRoutes}</h4>
                                        </div>
                                    </div>
                                </div>
                            </div>
                            ${routesHtml}
                        </div>
                    `,
                    width: 800
                });
            })
            .catch(error => {
                console.error('Error loading driver details:', error);
                Swal.fire('Error!', 'Failed to load driver details.', 'error');
            });
    }

    // Toggle car details row
    function toggleCarDetails(carId) {
        const detailsRow = $(`#car-details-${carId}`);
        const expandBtn = $(`.car-row[data-car-id="${carId}"] .expand-btn i`);
        
        if (detailsRow.is(':visible')) {
            detailsRow.hide();
            expandBtn.removeClass('bi-chevron-down').addClass('bi-chevron-right');
        } else {
            detailsRow.show();
            expandBtn.removeClass('bi-chevron-right').addClass('bi-chevron-down');
            
            // Load details if not already loaded
            if (detailsRow.data('loaded') !== true) {
                loadCarDetailsInline(carId);
            }
        }
    }

    // Load car details inline
    function loadCarDetailsInline(carId) {
        const detailsCell = $(`#car-details-${carId} td`);
        
        fetch(`/getCarDetails/${carId}`)
            .then(response => response.json())
            .then(data => {
                $(`.route-count-car-${carId}`).text(data.totalRoutes);
                
                if (data.activeRoutes > 0) {
                    $(`.car-status-${carId}`).removeClass('bg-success').addClass('bg-warning').text('In Use');
                } else {
                    $(`.car-status-${carId}`).removeClass('bg-warning').addClass('bg-success').text('Available');
                }
                
                // VIN decoded info
                let vinHtml = '<p class="text-muted mt-3">No VIN data available.</p>';
                if (data.vinDecoded && data.vinDecoded.raw) {
                    const v = data.vinDecoded;
                    vinHtml = `
                        <h6 class="mt-3"><i class="bi bi-upc-scan"></i> Vehicle Identification (VIN)</h6>
                        <div class="row g-2">
                            <div class="col-md-12 mb-2"><code style="font-size:1.1rem;letter-spacing:2px">${v.raw}</code></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2"><small class="text-muted">Manufacturer</small><h6 class="mb-0">${v.manufacturer || 'N/A'}</h6></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2"><small class="text-muted">Country</small><h6 class="mb-0">${v.country || 'N/A'}</h6></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2"><small class="text-muted">Vehicle Type</small><h6 class="mb-0">${v.vehicleType || 'N/A'}</h6></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2"><small class="text-muted">Model Year</small><h6 class="mb-0">${v.modelYear || 'N/A'}</h6></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2"><small class="text-muted">Plant Code</small><h6 class="mb-0">${v.plantCode || 'N/A'}</h6></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2"><small class="text-muted">Serial Number</small><h6 class="mb-0">${v.serialNumber || 'N/A'}</h6></div></div></div>
                        </div>
                    `;
                }

                // Vehicle status: fuel, battery
                let statusHtml = '';
                if (data.latestData) {
                    const d = data.latestData;
                    statusHtml = `
                        <h6 class="mt-4"><i class="bi bi-fuel-pump"></i> Vehicle Status</h6>
                        <p><small class="text-muted">Last updated: ${new Date(d.timestamp).toLocaleString()}</small></p>
                        <div class="row g-2">
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2 text-center"><small class="text-muted">Fuel Type</small><h5 class="mb-0">${d.fuelType || 'N/A'}</h5></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2 text-center"><small class="text-muted">Fuel Level</small><h5 class="mb-0">${d.fuelLevel ? parseFloat(d.fuelLevel).toFixed(1) + '%' : 'N/A'}</h5></div></div></div>
                            <div class="col-md-4"><div class="card bg-light"><div class="card-body p-2 text-center"><small class="text-muted">Battery</small><h5 class="mb-0">${d.batteryVoltage || 'N/A'}</h5></div></div></div>
                        </div>
                    `;
                }

                // Recent data log
                let recentDataHtml = '';
                if (data.recentCarData && data.recentCarData.length > 0) {
                    recentDataHtml = `
                        <h6 class="mt-4"><i class="bi bi-clock-history"></i> Recent Telemetry Log</h6>
                        <div class="table-responsive" style="max-height: 200px; overflow-y: auto;">
                            <table class="table table-sm table-bordered">
                                <thead class="sticky-top bg-white">
                                    <tr><th>Time</th><th>RPM</th><th>Speed</th><th>Temp</th><th>Fuel</th><th>Battery</th></tr>
                                </thead>
                                <tbody>
                                    ${data.recentCarData.map(cd => `
                                        <tr>
                                            <td><small>${new Date(cd.timestamp).toLocaleTimeString()}</small></td>
                                            <td>${cd.rpm || '-'}</td>
                                            <td>${cd.speed || '-'} km/h</td>
                                            <td>${cd.engineCoolantTemperature || '-'}°C</td>
                                            <td>${cd.fuelLevel ? parseFloat(cd.fuelLevel).toFixed(1) + '%' : '-'}</td>
                                            <td>${cd.batteryVoltage || '-'}</td>
                                        </tr>
                                    `).join('')}
                                </tbody>
                            </table>
                        </div>
                    `;
                }

                detailsCell.html(`
                    <div class="p-3 bg-light">
                        <h5><i class="bi bi-truck"></i> ${data.serialNumber}</h5>
                        <span class="badge bg-info">${data.totalRoutes} Routes</span>
                        <span class="badge ${data.activeRoutes > 0 ? 'bg-warning' : 'bg-success'}">${data.activeRoutes > 0 ? 'In Use' : 'Available'}</span>
                        ${vinHtml}
                        ${statusHtml}
                        ${recentDataHtml}
                    </div>
                `);
                
                $(`#car-details-${carId}`).data('loaded', true);
            })
            .catch(error => {
                console.error('Error loading car details:', error);
                detailsCell.html(`
                    <div class="alert alert-danger m-3">
                        Failed to load vehicle details. Please try again.
                    </div>
                `);
            });
    }

    // View car details in modal
    function viewCarDetails(carId) {
        fetch(`/getCarDetails/${carId}`)
            .then(response => response.json())
            .then(data => {
                let vinHtml = '';
                if (data.vinDecoded && data.vinDecoded.raw) {
                    const v = data.vinDecoded;
                    vinHtml = `
                        <h6 class="mt-3">VIN Info:</h6>
                        <p><code>${v.raw}</code></p>
                        <p>${v.manufacturer || 'N/A'} | ${v.country || 'N/A'} | ${v.modelYear || 'N/A'}</p>
                    `;
                }
                let statusHtml = '';
                if (data.latestData) {
                    const d = data.latestData;
                    statusHtml = `
                        <h6 class="mt-3">Vehicle Status:</h6>
                        <p><strong>Fuel Type:</strong> ${d.fuelType || 'N/A'} | <strong>Fuel Level:</strong> ${d.fuelLevel ? parseFloat(d.fuelLevel).toFixed(1) + '%' : 'N/A'} | <strong>Battery:</strong> ${d.batteryVoltage || 'N/A'}</p>
                        <p class="text-muted"><small>Last updated: ${new Date(d.timestamp).toLocaleString()}</small></p>
                    `;
                }
                
                Swal.fire({
                    title: `Vehicle: ${data.serialNumber}`,
                    html: `
                        <div class="text-start">
                            <div class="row">
                                <div class="col-4">
                                    <div class="card bg-primary text-white">
                                        <div class="card-body p-2">
                                            <small>Total Routes</small>
                                            <h4>${data.totalRoutes}</h4>
                                        </div>
                                    </div>
                                </div>
                                <div class="col-4">
                                    <div class="card bg-${data.activeRoutes > 0 ? 'warning' : 'success'} text-white">
                                        <div class="card-body p-2">
                                            <small>Active Routes</small>
                                            <h4>${data.activeRoutes}</h4>
                                        </div>
                                    </div>
                                </div>
                                <div class="col-4">
                                    <div class="card bg-secondary text-white">
                                        <div class="card-body p-2">
                                            <small>Status</small>
                                            <h6>${data.activeRoutes > 0 ? 'In Use' : 'Available'}</h6>
                                        </div>
                                    </div>
                                </div>
                            </div>
                            ${vinHtml}
                            ${statusHtml}
                        </div>
                    `,
                    width: 800
                });
            })
            .catch(error => {
                console.error('Error loading car details:', error);
                Swal.fire('Error!', 'Failed to load vehicle details.', 'error');
            });
    }

    // Delete functions
    function deleteDriver(id) {
        Swal.fire({
            title: 'Are you sure?',
            text: "This will delete the driver permanently!",
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#d33',
            cancelButtonColor: '#3085d6',
            confirmButtonText: 'Yes, delete it!'
        }).then((result) => {
            if (result.isConfirmed) {
                $.ajax({
                    url: `/deleteUser/${id}`,
                    type: 'DELETE',
                    success: function() {
                        Swal.fire('Deleted!', 'Driver has been deleted.', 'success');
                        loadDrivers();
                        loadDashboardStats();
                    },
                    error: function() {
                        Swal.fire('Error!', 'Failed to delete driver.', 'error');
                    }
                });
            }
        });
    }

    function deleteCar(id) {
        Swal.fire({
            title: 'Are you sure?',
            text: "This will delete the vehicle permanently!",
            icon: 'warning',
            showCancelButton: true,
            confirmButtonColor: '#d33',
            cancelButtonColor: '#3085d6',
            confirmButtonText: 'Yes, delete it!'
        }).then((result) => {
            if (result.isConfirmed) {
                $.ajax({
                    url: `/deleteCar/${id}`,
                    type: 'DELETE',
                    success: function() {
                        Swal.fire('Deleted!', 'Vehicle has been deleted.', 'success');
                        loadCars();
                        loadDashboardStats();
                    },
                    error: function() {
                        Swal.fire('Error!', 'Failed to delete vehicle.', 'error');
                    }
                });
            }
        });
    }

    function editDriver(id) {
        Swal.fire('Info', 'Edit functionality coming soon', 'info');
    }

    function editCar(id) {
        Swal.fire('Info', 'Edit functionality coming soon', 'info');
    }